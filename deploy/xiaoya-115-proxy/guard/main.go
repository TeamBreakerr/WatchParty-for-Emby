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
	"strings"
	"sync"
	"sync/atomic"
	"syscall"
	"time"
)

const (
	targetHeader    = "X-Emby-115-Target"
	keyHeader       = "X-Emby-115-Key"
	guardHeader     = "X-Emby-115-Guard"
	stableUserAgent = "Emby-Xiaoya-Proxy/1.0"
)

var keyPattern = regexp.MustCompile(`^[a-f0-9]{32}$`)

var errSlotWaitTimeout = errors.New("no upstream lease became available before the wait deadline")

var allowed115Suffixes = []string{"115cdn.net", "115cdn.com", "115.com"}

type targetParser func(string) (*url.URL, error)

type streamLease struct {
	id     uint64
	cancel context.CancelFunc
}

type leaseManager struct {
	mu         sync.Mutex
	nextID     uint64
	byKey      map[string]*leaseGroup
	maxPerKey  int
	waitTime   time.Duration
	closeGrace time.Duration
}

type leaseGroup struct {
	leases      []*streamLease
	slotChanged chan struct{}
}

func newLeaseManager(maxPerKey int, waitTime, closeGrace time.Duration) *leaseManager {
	return &leaseManager{
		byKey:      make(map[string]*leaseGroup),
		maxPerKey:  maxPerKey,
		waitTime:   waitTime,
		closeGrace: closeGrace,
	}
}

func (m *leaseManager) acquire(parent context.Context, key string) (context.Context, *streamLease, bool, error) {
	ctx, cancel := context.WithCancel(parent)
	waited := false
	var waitDeadline time.Time
	lease := &streamLease{
		id:     atomic.AddUint64(&m.nextID, 1),
		cancel: cancel,
	}

	for {
		m.mu.Lock()
		group := m.byKey[key]
		if group == nil {
			group = &leaseGroup{slotChanged: make(chan struct{})}
			m.byKey[key] = group
		}
		if len(group.leases) < m.maxPerKey {
			group.leases = append(group.leases, lease)
			m.mu.Unlock()
			if waited {
				if err := m.waitForCloseGrace(parent); err != nil {
					m.release(key, lease)
					return nil, nil, true, err
				}
			}
			return ctx, lease, waited, nil
		}
		slotChanged := group.slotChanged
		m.mu.Unlock()

		if !waited {
			waited = true
			waitDeadline = time.Now().Add(m.waitTime)
		}
		if err := waitForSlot(parent, slotChanged, time.Until(waitDeadline)); err != nil {
			cancel()
			return nil, nil, true, err
		}
	}
}

func waitForSlot(parent context.Context, slotChanged <-chan struct{}, waitTime time.Duration) error {
	if waitTime <= 0 {
		select {
		case <-slotChanged:
			return nil
		case <-parent.Done():
			return parent.Err()
		default:
			return errSlotWaitTimeout
		}
	}
	timer := time.NewTimer(waitTime)
	defer timer.Stop()
	select {
	case <-slotChanged:
		return nil
	case <-timer.C:
		// Prefer a release that raced the timer. This avoids returning 503 when
		// the upstream finished at the end of the bounded wait window.
		select {
		case <-slotChanged:
			return nil
		default:
			return errSlotWaitTimeout
		}
	case <-parent.Done():
		return parent.Err()
	}
}

func (m *leaseManager) waitForCloseGrace(parent context.Context) error {
	if m.closeGrace <= 0 {
		return nil
	}
	timer := time.NewTimer(m.closeGrace)
	defer timer.Stop()
	select {
	case <-timer.C:
		return nil
	case <-parent.Done():
		return parent.Err()
	}
}

func removeLease(leases []*streamLease, id uint64) []*streamLease {
	for index, candidate := range leases {
		if candidate.id == id {
			return append(leases[:index], leases[index+1:]...)
		}
	}
	return leases
}

func (m *leaseManager) release(key string, lease *streamLease) {
	lease.cancel()

	m.mu.Lock()
	defer m.mu.Unlock()
	group := m.byKey[key]
	if group == nil {
		return
	}
	remaining := removeLease(group.leases, lease.id)
	if len(remaining) == len(group.leases) {
		return
	}
	group.leases = remaining
	close(group.slotChanged)
	group.slotChanged = make(chan struct{})
	if len(group.leases) == 0 {
		delete(m.byKey, key)
	}
}

type streamGuard struct {
	client    *http.Client
	parse     targetParser
	leases    *leaseManager
	logger    *log.Logger
	evictions atomic.Uint64
	slotWaits atomic.Uint64
	timeouts  atomic.Uint64
	breaches  atomic.Uint64
	activeMu  sync.Mutex
	active    map[string]int
}

func newStreamGuard(client *http.Client, parse targetParser, logger *log.Logger) *streamGuard {
	return &streamGuard{
		client: client,
		parse:  parse,
		leases: newLeaseManager(2, 750*time.Millisecond, 250*time.Millisecond),
		logger: logger,
		active: make(map[string]int),
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

	leaseContext, lease, waited, acquireErr := g.leases.acquire(r.Context(), key)
	if waited {
		g.slotWaits.Add(1)
	}
	if acquireErr != nil {
		if errors.Is(acquireErr, context.Canceled) || errors.Is(acquireErr, context.DeadlineExceeded) {
			return
		}
		g.timeouts.Add(1)
		g.logger.Printf("slot wait timed out for key %.8s", key)
		w.Header().Set("Retry-After", "1")
		http.Error(w, "115 upstream slot unavailable", http.StatusServiceUnavailable)
		return
	}
	defer g.leases.release(key, lease)
	// A full key waits for a natural release. It never cancels an existing
	// stream, because doing so truncates a healthy HLS/Range response and causes
	// the media server to restart the whole playback pipeline.
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

	upstreamDone := g.beginUpstream(key)
	response, err := g.client.Do(upstreamRequest)
	if err != nil {
		upstreamDone()
		if leaseContext.Err() == nil {
			g.logger.Printf("upstream request failed for host %s: category=%s",
				target.Hostname(), errorCategory(err))
			http.Error(w, "115 upstream unavailable", http.StatusBadGateway)
		}
		return
	}
	defer upstreamDone()

	copyDone := make(chan struct{})
	go func() {
		select {
		case <-leaseContext.Done():
			_ = response.Body.Close()
		case <-copyDone:
		}
	}()
	defer close(copyDone)
	defer func() {
		_ = response.Body.Close()
	}()

	copyResponseHeaders(w.Header(), response.Header)
	w.Header().Set(guardHeader, "active")
	w.WriteHeader(response.StatusCode)
	if flusher, ok := w.(http.Flusher); ok {
		flusher.Flush()
	}

	buffer := make([]byte, 64*1024)
	if _, err := io.CopyBuffer(w, response.Body, buffer); err != nil && leaseContext.Err() == nil {
		g.logger.Printf("downstream copy ended for host %s: category=%s",
			target.Hostname(), errorCategory(err))
	}
}

func (g *streamGuard) beginUpstream(key string) func() {
	g.activeMu.Lock()
	g.active[key]++
	if g.active[key] > g.leases.maxPerKey {
		g.breaches.Add(1)
	}
	g.activeMu.Unlock()

	var once sync.Once
	return func() {
		once.Do(func() {
			g.activeMu.Lock()
			g.active[key]--
			if g.active[key] == 0 {
				delete(g.active, key)
			}
			g.activeMu.Unlock()
		})
	}
}

func (g *streamGuard) writeMetrics(w http.ResponseWriter) {
	w.Header().Set("Content-Type", "text/plain; version=0.0.4; charset=utf-8")
	_, _ = fmt.Fprintf(w,
		"emby_115_guard_replacements_total %d\n"+
			"emby_115_guard_slot_waits_total %d\n"+
			"emby_115_guard_replacement_timeouts_total %d\n"+
			"emby_115_guard_slot_wait_timeouts_total %d\n"+
			"emby_115_guard_connection_limit_breaches_total %d\n",
		g.evictions.Load(), g.slotWaits.Load(), g.timeouts.Load(), g.timeouts.Load(), g.breaches.Load())
}

func errorCategory(err error) string {
	if errors.Is(err, context.Canceled) {
		return "canceled"
	}
	if errors.Is(err, context.DeadlineExceeded) {
		return "timeout"
	}
	var urlErr *url.Error
	if errors.As(err, &urlErr) {
		err = urlErr.Err
	}
	var dnsErr *net.DNSError
	if errors.As(err, &dnsErr) {
		return "dns"
	}
	var networkErr net.Error
	if errors.As(err, &networkErr) {
		if networkErr.Timeout() {
			return "timeout"
		}
		return "network"
	}
	if errors.Is(err, io.ErrUnexpectedEOF) {
		return "unexpected_eof"
	}
	return "upstream"
}

func copyRequestHeaders(destination, source http.Header) {
	for _, name := range []string{
		"Accept",
		"If-Range",
		"Icy-MetaData",
		"Range",
	} {
		if value := source.Get(name); value != "" {
			destination.Set(name, value)
		}
	}
	destination.Set("User-Agent", stableUserAgent)
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
	for _, suffix := range allowed115Suffixes {
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
	mux.HandleFunc("/metrics", func(w http.ResponseWriter, _ *http.Request) {
		guard.writeMetrics(w)
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
