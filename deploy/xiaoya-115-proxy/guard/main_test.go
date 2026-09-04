package main

import (
	"bytes"
	"context"
	"crypto/md5"
	"crypto/tls"
	"errors"
	"fmt"
	"io"
	"log"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

type roundTripFunc func(*http.Request) (*http.Response, error)

func (fn roundTripFunc) RoundTrip(request *http.Request) (*http.Response, error) {
	return fn(request)
}

type delayedCloseBody struct {
	active       *atomic.Int32
	closeStarted chan struct{}
	releaseClose <-chan struct{}
	startOnce    sync.Once
	finishOnce   sync.Once
}

func (b *delayedCloseBody) Read(_ []byte) (int, error) {
	<-b.closeStarted
	return 0, io.EOF
}

func (b *delayedCloseBody) Close() error {
	b.startOnce.Do(func() {
		close(b.closeStarted)
	})
	<-b.releaseClose
	b.finishOnce.Do(func() {
		b.active.Add(-1)
	})
	return nil
}

// testClock drives pacing without real time, so the tests assert the schedule
// the manager computes rather than how long a machine happened to sleep.
type testClock struct {
	mu      sync.Mutex
	current time.Time
	slept   []time.Duration
}

func newTestClock() *testClock {
	return &testClock{current: time.Unix(0, 0)}
}

func (c *testClock) now() time.Time {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.current
}

// sleep records the delay without advancing the clock, so a test can model
// callers that all arrive at the same instant.
func (c *testClock) sleep(ctx context.Context, d time.Duration) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	c.mu.Lock()
	defer c.mu.Unlock()
	c.slept = append(c.slept, d)
	return nil
}

// sleptFor returns the most recent delay, or zero when nothing slept.
func (c *testClock) sleptFor() time.Duration {
	c.mu.Lock()
	defer c.mu.Unlock()
	if len(c.slept) == 0 {
		return 0
	}
	return c.slept[len(c.slept)-1]
}

func newPacedLeaseManager(clock *testClock, gap, waitTime time.Duration) *leaseManager {
	manager := newLeaseManager(maxConcurrentPerKey, waitTime, 0)
	manager.startGap = gap
	manager.now = clock.now
	manager.sleep = clock.sleep
	return manager
}

// reservedGap reports how far ahead of now the next turn has been reserved.
func (m *leaseManager) reservedGap(key string) time.Duration {
	m.mu.Lock()
	defer m.mu.Unlock()
	group := m.byKey[key]
	if group == nil {
		return 0
	}
	return group.nextStartAt.Sub(m.now())
}

func TestParse115Target(t *testing.T) {
	t.Parallel()

	valid := []string{
		"https://cdnfhnfile.115cdn.net/video.mkv?token=redacted",
		"https://115cdn.net/video.mkv",
		"https://edge.115cdn.com/video.mkv",
		"https://download.115.com:443/video.mkv",
	}
	for _, target := range valid {
		if _, err := parse115Target(target); err != nil {
			t.Fatalf("expected %q to be valid: %v", target, err)
		}
	}

	invalid := []string{
		"http://cdnfhnfile.115cdn.net/video.mkv",
		"https://not115cdn.net/video.mkv",
		"https://115cdn.net.example.com/video.mkv",
		"https://user@cdnfhnfile.115cdn.net/video.mkv",
		"https://cdnfhnfile.115cdn.net:8443/video.mkv",
		"https://127.0.0.1/video.mkv",
	}
	for _, target := range invalid {
		if _, err := parse115Target(target); err == nil {
			t.Fatalf("expected %q to be rejected", target)
		}
	}
}

func TestGuardUsesStableUserAgentForSignedDownload(t *testing.T) {
	t.Parallel()

	var observedUserAgent string
	transport := roundTripFunc(func(request *http.Request) (*http.Response, error) {
		observedUserAgent = request.Header.Get("User-Agent")
		return &http.Response{
			StatusCode: http.StatusPartialContent,
			Header: http.Header{
				"Content-Type":  []string{"application/octet-stream"},
				"Content-Range": []string{"bytes 0-0/1"},
			},
			Body:    io.NopCloser(strings.NewReader("x")),
			Request: request,
		}, nil
	})
	target, err := url.Parse("https://cdnfhnfile.115cdn.net/video.mkv?token=redacted")
	if err != nil {
		t.Fatal(err)
	}
	guard := newStreamGuard(
		&http.Client{Transport: transport},
		func(string) (*url.URL, error) { return target, nil },
		log.New(io.Discard, "", 0))

	request := httptest.NewRequest(http.MethodGet, "http://guard/stream", nil)
	request.Header.Set(targetHeader, target.String())
	request.Header.Set(keyHeader, fmt.Sprintf("%x", md5.Sum([]byte("/media/video.mkv"))))
	request.Header.Set("User-Agent", "mismatched-client-agent")
	response := httptest.NewRecorder()
	guard.ServeHTTP(response, request)

	if response.Code != http.StatusPartialContent {
		t.Fatalf("expected 206, got %d", response.Code)
	}
	if observedUserAgent != stableUserAgent {
		t.Fatalf("expected stable user agent %q, got %q", stableUserAgent, observedUserAgent)
	}
}

func TestConcurrentStreamsStartSpacedWithoutEviction(t *testing.T) {
	// The provider refuses reads that begin together, so the invariant is the
	// gap between starts - not a ceiling on how many run at once. A third
	// reader must therefore be served while the first two are still streaming.
	var active atomic.Int32
	var maximum atomic.Int32
	var startMu sync.Mutex
	var startedAt []time.Time

	upstream := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		startMu.Lock()
		startedAt = append(startedAt, time.Now())
		startMu.Unlock()
		current := active.Add(1)
		defer active.Add(-1)
		for {
			observed := maximum.Load()
			if current <= observed || maximum.CompareAndSwap(observed, current) {
				break
			}
		}

		w.Header().Set("Content-Type", "application/octet-stream")
		w.Header().Set("Content-Range", "bytes 0-1023/4096")
		w.WriteHeader(http.StatusPartialContent)
		if flusher, ok := w.(http.Flusher); ok {
			flusher.Flush()
		}

		if r.Header.Get("Range") == "bytes=200-299" {
			_, _ = w.Write(make([]byte, 100))
			return
		}

		ticker := time.NewTicker(10 * time.Millisecond)
		defer ticker.Stop()
		for {
			select {
			case <-r.Context().Done():
				return
			case <-ticker.C:
				if _, err := w.Write(make([]byte, 32*1024)); err != nil {
					return
				}
				if flusher, ok := w.(http.Flusher); ok {
					flusher.Flush()
				}
			}
		}
	}))
	defer upstream.Close()

	upstreamURL, err := url.Parse(upstream.URL)
	if err != nil {
		t.Fatal(err)
	}
	parser := func(rawTarget string) (*url.URL, error) {
		if rawTarget != upstream.URL+"/video" {
			return nil, fmt.Errorf("unexpected target")
		}
		return upstreamURL.JoinPath("video"), nil
	}
	upstreamClient := upstream.Client()
	upstreamClient.Transport.(*http.Transport).TLSClientConfig = &tls.Config{ //nolint:gosec
		InsecureSkipVerify: true,
	}

	guard := newStreamGuard(upstreamClient, parser, log.New(io.Discard, "", 0))
	const gap = 80 * time.Millisecond
	guard.leases.startGap = gap
	guardServer := httptest.NewServer(guard)
	defer guardServer.Close()

	key := fmt.Sprintf("%x", md5.Sum([]byte("/media/video.mkv")))
	first := openGuardStream(t, guardServer.URL, upstream.URL+"/video", key, "bytes=0-")
	defer first.Body.Close()
	second := openGuardStream(t, guardServer.URL, upstream.URL+"/video", key, "bytes=100-")
	defer second.Body.Close()

	deadline := time.Now().Add(2 * time.Second)
	for active.Load() != 2 && time.Now().Before(deadline) {
		time.Sleep(5 * time.Millisecond)
	}
	if active.Load() != 2 {
		t.Fatalf("expected two active upstreams, got %d", active.Load())
	}

	// The third reader proceeds on its own turn, with both others still open.
	third := openGuardStream(
		t, guardServer.URL, upstream.URL+"/video", key, "bytes=200-299")
	thirdBody, err := io.ReadAll(third.Body)
	third.Body.Close()
	if err != nil {
		t.Fatal(err)
	}
	if third.StatusCode != http.StatusPartialContent || len(thirdBody) != 100 {
		t.Fatalf("unexpected third response: status=%d bytes=%d",
			third.StatusCode, len(thirdBody))
	}
	if active.Load() < 2 {
		t.Fatal("the third read was only served after an earlier one ended")
	}
	if maximum.Load() < 3 {
		t.Fatalf("three concurrent upstreams were expected, peaked at %d",
			maximum.Load())
	}

	startMu.Lock()
	observed := append([]time.Time(nil), startedAt...)
	startMu.Unlock()
	if len(observed) < 3 {
		t.Fatalf("expected three upstream starts, got %d", len(observed))
	}
	for i := 1; i < len(observed); i++ {
		if elapsed := observed[i].Sub(observed[i-1]); elapsed < gap/2 {
			t.Fatalf("starts %d and %d were only %s apart, want at least %s",
				i-1, i, elapsed, gap/2)
		}
	}
	if guard.evictions.Load() != 0 {
		t.Fatalf("expected no forced evictions, got %d", guard.evictions.Load())
	}

	first.Body.Close()
	second.Body.Close()
}

func TestSimultaneousAcquisitionsAreSpacedAndBounded(t *testing.T) {
	// Callers that arrive together must leave with evenly spaced starts, and a
	// caller whose turn falls beyond the wait window must be refused rather
	// than held indefinitely.
	clock := newTestClock()
	manager := newPacedLeaseManager(clock, 50*time.Millisecond, 120*time.Millisecond)
	key := "0123456789abcdef0123456789abcdef"

	var delays []time.Duration
	for range 3 {
		_, lease, waited, err := manager.acquire(t.Context(), key)
		if err != nil {
			t.Fatalf("acquisition was refused inside the wait window: %v", err)
		}
		if lease == nil {
			t.Fatal("no lease returned")
		}
		delays = append(delays, clock.sleptFor())
		if (delays[len(delays)-1] > 0) != waited {
			t.Fatalf("waited flag %v disagrees with delay %s",
				waited, delays[len(delays)-1])
		}
	}

	want := []time.Duration{0, 50 * time.Millisecond, 100 * time.Millisecond}
	for i, delay := range delays {
		if delay != want[i] {
			t.Fatalf("start %d paced by %s, want %s", i, delay, want[i])
		}
	}

	// The fourth turn lands past the wait window.
	_, lease, _, err := manager.acquire(t.Context(), key)
	if !errors.Is(err, errSlotWaitTimeout) || lease != nil {
		t.Fatalf("expected a bounded refusal, got lease=%v err=%v", lease, err)
	}
	// A refusal must not consume a turn, or every later caller pays for it.
	if reserved := manager.reservedGap(key); reserved != 150*time.Millisecond {
		t.Fatalf("refused caller consumed a turn: reservation at %s", reserved)
	}
}

func TestLeaseManagerGrantsWaitersInFIFOOrder(t *testing.T) {
	// The resource bound is the only thing that queues callers now. When it
	// frees up, the queue must still be served in arrival order.
	manager := newLeaseManager(2, time.Second, 0)
	manager.startGap = 0
	key := "0123456789abcdef0123456789abcdef"

	_, first, _, err := manager.acquire(t.Context(), key)
	if err != nil {
		t.Fatal(err)
	}
	_, second, _, err := manager.acquire(t.Context(), key)
	if err != nil {
		t.Fatal(err)
	}

	type acquisition struct {
		lease *streamLease
		err   error
	}
	firstWaiter := make(chan acquisition, 1)
	secondWaiter := make(chan acquisition, 1)
	go func() {
		_, lease, _, acquireErr := manager.acquire(t.Context(), key)
		firstWaiter <- acquisition{lease: lease, err: acquireErr}
	}()
	waitForQueuedWaiters(t, manager, key, 1)
	go func() {
		_, lease, _, acquireErr := manager.acquire(t.Context(), key)
		secondWaiter <- acquisition{lease: lease, err: acquireErr}
	}()
	waitForQueuedWaiters(t, manager, key, 2)

	manager.release(key, first)
	var firstResult acquisition
	select {
	case firstResult = <-firstWaiter:
		if firstResult.err != nil || firstResult.lease == nil {
			t.Fatalf("first waiter was not granted: lease=%v err=%v",
				firstResult.lease, firstResult.err)
		}
	case result := <-secondWaiter:
		t.Fatalf("second waiter overtook the first: lease=%v err=%v",
			result.lease, result.err)
	case <-time.After(250 * time.Millisecond):
		t.Fatal("first waiter was not granted after a slot release")
	}

	manager.release(key, second)
	var secondResult acquisition
	select {
	case secondResult = <-secondWaiter:
		if secondResult.err != nil || secondResult.lease == nil {
			t.Fatalf("second waiter was not granted: lease=%v err=%v",
				secondResult.lease, secondResult.err)
		}
	case <-time.After(250 * time.Millisecond):
		t.Fatal("second waiter was not granted after the next slot release")
	}

	manager.release(key, firstResult.lease)
	manager.release(key, secondResult.lease)
}

func TestGrantedWaiterIsPacedOnlyWhileAReadOverlaps(t *testing.T) {
	// A grant only says the resource bound has room. If another read of the
	// same media is still running, the new one would start alongside it and
	// must be spaced; if the queue drained because everything finished, there
	// is nothing to collide with and it starts at once.
	clock := newTestClock()
	manager := newPacedLeaseManager(clock, 40*time.Millisecond, time.Second)
	manager.maxPerKey = 2
	key := "0123456789abcdef0123456789abcdef"

	_, first, _, err := manager.acquire(t.Context(), key)
	if err != nil {
		t.Fatal(err)
	}
	_, second, _, err := manager.acquire(t.Context(), key)
	if err != nil {
		t.Fatal(err)
	}

	type grant struct {
		lease *streamLease
		delay time.Duration
	}
	granted := make(chan grant, 1)
	go func() {
		_, lease, _, acquireErr := manager.acquire(t.Context(), key)
		if acquireErr != nil {
			granted <- grant{delay: -1}
			return
		}
		granted <- grant{lease: lease, delay: clock.sleptFor()}
	}()
	waitForQueuedWaiters(t, manager, key, 1)

	// `second` keeps running, so the granted waiter overlaps it and must be
	// spaced. The clock is frozen, so all three callers share one arrival
	// instant and are scheduled at 0, one gap and two gaps.
	manager.release(key, first)
	var promoted *streamLease
	select {
	case result := <-granted:
		if result.delay != 80*time.Millisecond {
			t.Fatalf("waiter overlapping a live read started after %s, "+
				"want the third slot on the shared schedule", result.delay)
		}
		promoted = result.lease
	case <-time.After(time.Second):
		t.Fatal("granted waiter never started")
	}

	manager.release(key, second)
	manager.release(key, promoted)

	// Nothing is in flight now, so the next reader must not be delayed.
	_, lone, waited, err := manager.acquire(t.Context(), key)
	if err != nil {
		t.Fatal(err)
	}
	if waited {
		t.Fatal("a lone reader was paced against nothing")
	}
	manager.release(key, lone)
}

func waitForQueuedWaiters(t *testing.T, manager *leaseManager, key string, count int) {
	t.Helper()
	deadline := time.Now().Add(250 * time.Millisecond)
	for time.Now().Before(deadline) {
		manager.mu.Lock()
		group := manager.byKey[key]
		queued := 0
		if group != nil {
			queued = len(group.waiters)
		}
		manager.mu.Unlock()
		if queued == count {
			return
		}
		time.Sleep(time.Millisecond)
	}
	t.Fatalf("expected %d queued waiter(s)", count)
}

func TestTurnBeyondTheWaitWindowIsRefusedWithoutOpeningUpstream(t *testing.T) {
	// A caller whose paced turn falls outside the wait window is told to retry
	// rather than held. It must not reach the provider, and it must never
	// disturb a read that is already running.
	var calls atomic.Int32
	var active atomic.Int32
	releaseClose := make(chan struct{})
	var releaseOnce sync.Once
	var closeMu sync.Mutex
	closeStarted := make(map[int32]chan struct{})

	transport := roundTripFunc(func(request *http.Request) (*http.Response, error) {
		callNumber := calls.Add(1)
		active.Add(1)
		closeMu.Lock()
		started := make(chan struct{})
		closeStarted[callNumber] = started
		closeMu.Unlock()
		return &http.Response{
			StatusCode: http.StatusPartialContent,
			Header: http.Header{
				"Content-Type":  []string{"application/octet-stream"},
				"Content-Range": []string{"bytes 0-1023/4096"},
			},
			Body: &delayedCloseBody{
				active:       &active,
				closeStarted: started,
				releaseClose: releaseClose,
			},
			Request: request,
		}, nil
	})

	target, err := url.Parse("https://cdnfhnfile.115cdn.net/video.mkv?token=redacted")
	if err != nil {
		t.Fatal(err)
	}
	guard := newStreamGuard(
		&http.Client{Transport: transport},
		func(string) (*url.URL, error) { return target, nil },
		log.New(io.Discard, "", 0))
	// Four callers arriving together are scheduled 0, 60, 120 and 180ms out;
	// only the first two fall inside the window.
	guard.leases.startGap = 60 * time.Millisecond
	guard.leases.waitTime = 100 * time.Millisecond
	guardServer := httptest.NewServer(guard)
	defer guardServer.Close()
	defer func() { releaseOnce.Do(func() { close(releaseClose) }) }()

	key := fmt.Sprintf("%x", md5.Sum([]byte("/media/video.mkv")))
	type outcome struct {
		status int
		retry  string
	}
	results := make(chan outcome, 4)
	var bodyMu sync.Mutex
	var bodies []io.ReadCloser
	defer func() {
		bodyMu.Lock()
		defer bodyMu.Unlock()
		for _, body := range bodies {
			body.Close()
		}
	}()
	var launch sync.WaitGroup
	launch.Add(1)
	var finished sync.WaitGroup
	for range 4 {
		finished.Add(1)
		go func() {
			defer finished.Done()
			request, requestErr := http.NewRequest(
				http.MethodGet, guardServer.URL+"/stream", nil)
			if requestErr != nil {
				results <- outcome{status: -1}
				return
			}
			request.Header.Set(targetHeader, target.String())
			request.Header.Set(keyHeader, key)
			request.Header.Set("Range", "bytes=0-")
			launch.Wait()
			response, responseErr := http.DefaultClient.Do(request)
			if responseErr != nil {
				results <- outcome{status: -1}
				return
			}
			// Leave a served body open: closing it would signal the upstream
			// close path, and the assertions below have to observe that no
			// healthy read was disturbed. The deferred release closes them.
			bodyMu.Lock()
			bodies = append(bodies, response.Body)
			bodyMu.Unlock()
			results <- outcome{
				status: response.StatusCode,
				retry:  response.Header.Get("Retry-After"),
			}
		}()
	}
	launch.Done()

	served, refused := 0, 0
	collected := make([]outcome, 0, 4)
	for range 4 {
		collected = append(collected, <-results)
	}
	finished.Wait()
	close(results)

	for _, result := range collected {
		switch result.status {
		case http.StatusPartialContent:
			served++
		case http.StatusServiceUnavailable:
			refused++
			if result.retry != "1" {
				t.Fatalf("expected a bounded retry hint, got %q", result.retry)
			}
		default:
			t.Fatalf("unexpected status %d", result.status)
		}
	}

	if refused == 0 {
		t.Fatal("no caller was refused, so the wait window is not bounded")
	}
	if served == 0 {
		t.Fatal("every caller was refused")
	}
	// A refusal must cost the provider nothing.
	if int(calls.Load()) != served {
		t.Fatalf("%d upstream attempts for %d served callers", calls.Load(), served)
	}
	// Refusing must never disturb a read that is already running.
	closeMu.Lock()
	for number, started := range closeStarted {
		select {
		case <-started:
			t.Fatalf("a refused caller closed healthy upstream %d", number)
		default:
		}
	}
	closeMu.Unlock()
	if guard.breaches.Load() != 0 {
		t.Fatalf("unexpected connection-limit breach: %d", guard.breaches.Load())
	}
	if int(guard.timeouts.Load()) != refused {
		t.Fatalf("timeouts=%d for %d refusals", guard.timeouts.Load(), refused)
	}
	metrics := httptest.NewRecorder()
	guard.writeMetrics(metrics)
	if !strings.Contains(metrics.Body.String(),
		fmt.Sprintf("emby_115_guard_slot_wait_timeouts_total %d\n", refused)) {
		t.Fatalf("slot wait timeout metric missing: %s", metrics.Body.String())
	}
}

func TestUpstreamFailureLogsDoNotContainSignedTargetURL(t *testing.T) {
	signedTarget := "https://cdnfhnfile.115cdn.net/video.mkv?token=super-secret"
	target, err := url.Parse(signedTarget)
	if err != nil {
		t.Fatal(err)
	}
	transport := roundTripFunc(func(request *http.Request) (*http.Response, error) {
		return nil, &url.Error{
			Op:  "Get",
			URL: request.URL.String(),
			Err: errors.New("dial failed"),
		}
	})
	var logs bytes.Buffer
	guard := newStreamGuard(
		&http.Client{Transport: transport},
		func(string) (*url.URL, error) { return target, nil },
		log.New(&logs, "", 0))

	request := httptest.NewRequest(http.MethodGet, "http://guard/stream", nil)
	request.Header.Set(targetHeader, signedTarget)
	request.Header.Set(keyHeader, fmt.Sprintf("%x", md5.Sum([]byte("/media/video.mkv"))))
	response := httptest.NewRecorder()
	guard.ServeHTTP(response, request)

	if response.Code != http.StatusBadGateway {
		t.Fatalf("expected 502 for an upstream failure, got %d", response.Code)
	}
	if strings.Contains(logs.String(), "super-secret") || strings.Contains(logs.String(), signedTarget) {
		t.Fatalf("signed target leaked into logs: %s", logs.String())
	}
	if !strings.Contains(logs.String(), "category=") {
		t.Fatalf("sanitized error category is missing: %s", logs.String())
	}
}

func openGuardStream(t *testing.T, guardURL, target, key, byteRange string) *http.Response {
	t.Helper()
	request, err := http.NewRequest(http.MethodGet, guardURL+"/stream", nil)
	if err != nil {
		t.Fatal(err)
	}
	request.Header.Set(targetHeader, target)
	request.Header.Set(keyHeader, key)
	request.Header.Set("Range", byteRange)
	response, err := http.DefaultClient.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	if response.Header.Get(guardHeader) != "active" {
		response.Body.Close()
		t.Fatal("guard response marker is missing")
	}
	return response
}
