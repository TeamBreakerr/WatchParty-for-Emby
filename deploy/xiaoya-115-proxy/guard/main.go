package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"net/url"
	"os"
	"os/signal"
	"regexp"
	"sort"
	"strings"
	"sync"
	"sync/atomic"
	"syscall"
	"time"
)

const (
	targetHeader = "X-Emby-115-Target"
	keyHeader    = "X-Emby-115-Key"
	guardHeader  = "X-Emby-115-Guard"
)

var keyPattern = regexp.MustCompile(`^[a-f0-9]{32}$`)

type targetParser func(string) (*url.URL, error)

type streamLease struct {
	id             uint64
	createdAt      time.Time
	cancel         context.CancelFunc
	upstreamClosed chan struct{}
	closeOnce      sync.Once
}

func (l *streamLease) markUpstreamClosed() {
	l.closeOnce.Do(func() {
		close(l.upstreamClosed)
	})
}

type leaseManager struct {
	mu        sync.Mutex
	nextID    uint64
	byKey     map[string][]*streamLease
	maxPerKey int
	waitTime  time.Duration
}

func newLeaseManager(maxPerKey int, waitTime time.Duration) *leaseManager {
	return &leaseManager{
		byKey:     make(map[string][]*streamLease),
		maxPerKey: maxPerKey,
		waitTime:  waitTime,
	}
}

func (m *leaseManager) acquire(parent context.Context, key string) (context.Context, *streamLease, bool) {
	ctx, cancel := context.WithCancel(parent)
	lease := &streamLease{
		id:             atomic.AddUint64(&m.nextID, 1),
		createdAt:      time.Now(),
		cancel:         cancel,
		upstreamClosed: make(chan struct{}),
	}

	m.mu.Lock()
	leases := m.byKey[key]
	sort.SliceStable(leases, func(i, j int) bool {
		return leases[i].createdAt.Before(leases[j].createdAt)
	})
	var evicted *streamLease
	if len(leases) >= m.maxPerKey {
		evicted = leases[0]
		leases = leases[1:]
		evicted.cancel()
	}
	m.byKey[key] = append(leases, lease)
	m.mu.Unlock()

	if evicted == nil {
		return ctx, lease, false
	}

	timer := time.NewTimer(m.waitTime)
	defer timer.Stop()
	select {
	case <-evicted.upstreamClosed:
	case <-timer.C:
	case <-ctx.Done():
	}
	return ctx, lease, true
}

func (m *leaseManager) release(key string, lease *streamLease) {
	lease.cancel()
	lease.markUpstreamClosed()

	m.mu.Lock()
	defer m.mu.Unlock()
	leases := m.byKey[key]
	for index, candidate := range leases {
		if candidate.id != lease.id {
			continue
		}
		leases = append(leases[:index], leases[index+1:]...)
		break
	}
	if len(leases) == 0 {
		delete(m.byKey, key)
	} else {
		m.byKey[key] = leases
	}
}

type streamGuard struct {
	client    *http.Client
	parse     targetParser
	leases    *leaseManager
	logger    *log.Logger
	evictions atomic.Uint64
}

func newStreamGuard(client *http.Client, parse targetParser, logger *log.Logger) *streamGuard {
	return &streamGuard{
		client: client,
		parse:  parse,
		leases: newLeaseManager(2, 2*time.Second),
		logger: logger,
	}
}

func (g *streamGuard) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	if r.URL.Path != "/stream" {
		http.NotFound(w, r)
		return
	}
	if r.Method != http.MethodGet {
		w.Header().Set("Allow", http.MethodGet)
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}

	key := r.Header.Get(keyHeader)
	if !keyPattern.MatchString(key) {
		http.Error(w, "invalid stream key", http.StatusBadRequest)
		return
	}
	target, err := g.parse(r.Header.Get(targetHeader))
	if err != nil {
		http.Error(w, "invalid 115 target", http.StatusBadRequest)
		return
	}

	leaseContext, lease, evicted := g.leases.acquire(r.Context(), key)
	defer g.leases.release(key, lease)
	if evicted {
		g.evictions.Add(1)
		g.logger.Printf("replaced oldest upstream stream for key %.8s", key)
	}
	if err := leaseContext.Err(); err != nil {
		return
	}

	upstreamRequest, err := http.NewRequestWithContext(
		leaseContext,
		http.MethodGet,
		target.String(),
		nil)
	if err != nil {
		http.Error(w, "could not create upstream request", http.StatusBadGateway)
		return
	}
	copyRequestHeaders(upstreamRequest.Header, r.Header)
	upstreamRequest.Host = target.Host

	response, err := g.client.Do(upstreamRequest)
	if err != nil {
		if leaseContext.Err() == nil {
			g.logger.Printf("upstream request failed for host %s: %v", target.Hostname(), err)
			http.Error(w, "115 upstream unavailable", http.StatusBadGateway)
		}
		return
	}

	copyDone := make(chan struct{})
	go func() {
		select {
		case <-leaseContext.Done():
			_ = response.Body.Close()
			lease.markUpstreamClosed()
		case <-copyDone:
		}
	}()
	defer close(copyDone)
	defer func() {
		_ = response.Body.Close()
		lease.markUpstreamClosed()
	}()

	copyResponseHeaders(w.Header(), response.Header)
	w.Header().Set(guardHeader, "active")
	w.WriteHeader(response.StatusCode)
	if flusher, ok := w.(http.Flusher); ok {
		flusher.Flush()
	}

	buffer := make([]byte, 64*1024)
	if _, err := io.CopyBuffer(w, response.Body, buffer); err != nil && leaseContext.Err() == nil {
		g.logger.Printf("downstream copy ended for host %s: %v", target.Hostname(), err)
	}
}

func copyRequestHeaders(destination, source http.Header) {
	for _, name := range []string{
		"Accept",
		"If-Range",
		"Icy-MetaData",
		"Range",
		"User-Agent",
	} {
		if value := source.Get(name); value != "" {
			destination.Set(name, value)
		}
	}
	destination.Set("Accept-Encoding", "identity")
}

func copyResponseHeaders(destination, source http.Header) {
	for name, values := range source {
		if isHopByHopHeader(name) {
			continue
		}
		for _, value := range values {
			destination.Add(name, value)
		}
	}
}

func isHopByHopHeader(name string) bool {
	switch http.CanonicalHeaderKey(name) {
	case "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
		"Te", "Trailer", "Transfer-Encoding", "Upgrade":
		return true
	default:
		return false
	}
}

func parse115Target(rawTarget string) (*url.URL, error) {
	if rawTarget == "" {
		return nil, errors.New("missing target")
	}
	target, err := url.Parse(rawTarget)
	if err != nil {
		return nil, err
	}
	if target.Scheme != "https" || target.User != nil || target.Fragment != "" {
		return nil, errors.New("target must be an https URL without credentials or a fragment")
	}
	if target.Port() != "" && target.Port() != "443" {
		return nil, errors.New("target port is not allowed")
	}
	host := strings.ToLower(strings.TrimSuffix(target.Hostname(), "."))
	if !is115Host(host) {
		return nil, errors.New("target host is not a 115 download host")
	}
	if target.Path == "" {
		return nil, errors.New("target path is missing")
	}
	return target, nil
}

func is115Host(host string) bool {
	for _, suffix := range []string{"115cdn.net", "115cdn.com", "115.com"} {
		if host == suffix || strings.HasSuffix(host, "."+suffix) {
			return true
		}
	}
	return false
}

func newUpstreamClient() *http.Client {
	dialer := &net.Dialer{
		Timeout:   10 * time.Second,
		KeepAlive: 15 * time.Second,
	}
	transport := &http.Transport{
		Proxy:                 http.ProxyFromEnvironment,
		DialContext:           dialer.DialContext,
		ForceAttemptHTTP2:     true,
		DisableCompression:    true,
		DisableKeepAlives:     true,
		TLSHandshakeTimeout:   10 * time.Second,
		ResponseHeaderTimeout: 15 * time.Second,
	}
	return &http.Client{
		Transport: transport,
		CheckRedirect: func(_ *http.Request, _ []*http.Request) error {
			return http.ErrUseLastResponse
		},
	}
}

func main() {
	listenAddress := flag.String("listen", "127.0.0.1:15678", "loopback listen address")
	flag.Parse()

	logger := log.New(os.Stderr, "emby-115-guard: ", log.LstdFlags|log.LUTC)
	guard := newStreamGuard(newUpstreamClient(), parse115Target, logger)
	mux := http.NewServeMux()
	mux.Handle("/stream", guard)
	mux.HandleFunc("/healthz", func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "text/plain; charset=utf-8")
		_, _ = io.WriteString(w, "ok\n")
	})

	server := &http.Server{
		Addr:              *listenAddress,
		Handler:           mux,
		ReadHeaderTimeout: 5 * time.Second,
		IdleTimeout:       30 * time.Second,
	}

	shutdownContext, stop := signal.NotifyContext(context.Background(), syscall.SIGINT, syscall.SIGTERM)
	defer stop()
	go func() {
		<-shutdownContext.Done()
		contextWithTimeout, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		_ = server.Shutdown(contextWithTimeout)
	}()

	logger.Printf("listening on %s", *listenAddress)
	if err := server.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
		logger.Fatal(fmt.Errorf("guard stopped: %w", err))
	}
}
