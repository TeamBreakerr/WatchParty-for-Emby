package main

import (
	"bytes"
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

func TestThirdStreamWaitsForNaturalReleaseWithoutEviction(t *testing.T) {
	var active atomic.Int32
	var maximum atomic.Int32

	upstream := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		current := active.Add(1)
		defer active.Add(-1)
		for {
			observed := maximum.Load()
			if current <= observed || maximum.CompareAndSwap(observed, current) {
				break
			}
		}
		if current > 2 {
			http.Error(w, "too many streams", http.StatusForbidden)
			return
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
	upstreamClient.Transport.(*http.Transport).DisableKeepAlives = true

	guard := newStreamGuard(upstreamClient, parser, log.New(io.Discard, "", 0))
	guardServer := httptest.NewServer(guard)
	defer guardServer.Close()

	key := fmt.Sprintf("%x", md5.Sum([]byte("/media/video.mkv")))
	first := openGuardStream(t, guardServer.URL, upstream.URL+"/video", key, "bytes=0-")
	defer first.Body.Close()
	second := openGuardStream(t, guardServer.URL, upstream.URL+"/video", key, "bytes=100-")
	defer second.Body.Close()

	deadline := time.Now().Add(time.Second)
	for active.Load() != 2 && time.Now().Before(deadline) {
		time.Sleep(5 * time.Millisecond)
	}
	if active.Load() != 2 {
		t.Fatalf("expected two active upstreams, got %d", active.Load())
	}

	thirdResult := make(chan *http.Response, 1)
	thirdErrors := make(chan error, 1)
	go func() {
		request, requestErr := http.NewRequest(
			http.MethodGet, guardServer.URL+"/stream", nil)
		if requestErr != nil {
			thirdErrors <- requestErr
			return
		}
		request.Header.Set(targetHeader, upstream.URL+"/video")
		request.Header.Set(keyHeader, key)
		request.Header.Set("Range", "bytes=200-299")
		response, responseErr := http.DefaultClient.Do(request)
		if responseErr != nil {
			thirdErrors <- responseErr
			return
		}
		thirdResult <- response
	}()

	select {
	case <-thirdResult:
		t.Fatal("third stream connected before a natural lease release")
	case err := <-thirdErrors:
		t.Fatal(err)
	case <-time.After(50 * time.Millisecond):
	}
	if guard.evictions.Load() != 0 {
		t.Fatalf("third stream caused an eviction while waiting: %d", guard.evictions.Load())
	}

	first.Body.Close()
	var third *http.Response
	select {
	case err := <-thirdErrors:
		t.Fatal(err)
	case third = <-thirdResult:
	case <-time.After(time.Second):
		t.Fatal("third stream did not proceed after the first lease released")
	}
	thirdBody, err := io.ReadAll(third.Body)
	third.Body.Close()
	if err != nil {
		t.Fatal(err)
	}
	if third.StatusCode != http.StatusPartialContent || len(thirdBody) != 100 {
		t.Fatalf("unexpected third response: status=%d bytes=%d", third.StatusCode, len(thirdBody))
	}
	if guard.evictions.Load() != 0 {
		t.Fatalf("expected no forced evictions, got %d", guard.evictions.Load())
	}
	if maximum.Load() > 2 {
		t.Fatalf("opened %d concurrent upstreams", maximum.Load())
	}

	first.Body.Close()
	second.Body.Close()
}

func TestConcurrentWaitersShareOneBoundedAcquisitionDeadline(t *testing.T) {
	manager := newLeaseManager(2, 200*time.Millisecond, 0)
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
	results := make(chan acquisition, 3)
	startedAt := time.Now()
	for range 3 {
		go func() {
			_, lease, _, acquireErr := manager.acquire(t.Context(), key)
			results <- acquisition{lease: lease, err: acquireErr}
		}()
	}

	time.Sleep(50 * time.Millisecond)
	manager.release(key, first)
	firstWinner := <-results
	if firstWinner.err != nil || firstWinner.lease == nil {
		t.Fatalf("first released slot was not acquired: %v", firstWinner.err)
	}

	time.Sleep(50 * time.Millisecond)
	manager.release(key, second)
	secondWinner := <-results
	if secondWinner.err != nil || secondWinner.lease == nil {
		t.Fatalf("second released slot was not acquired: %v", secondWinner.err)
	}

	select {
	case final := <-results:
		if !errors.Is(final.err, errSlotWaitTimeout) || final.lease != nil {
			t.Fatalf("expected the remaining waiter to time out, got lease=%v err=%v",
				final.lease, final.err)
		}
	case <-time.After(150 * time.Millisecond):
		t.Fatal("remaining waiter exceeded the original acquisition deadline")
	}
	if elapsed := time.Since(startedAt); elapsed > 250*time.Millisecond {
		t.Fatalf("bounded acquisition took %s", elapsed)
	}

	manager.release(key, firstWinner.lease)
	manager.release(key, secondWinner.lease)
}

func TestThirdStreamTimesOutWithoutOpeningUpstreamWhenNoSlotIsReleased(t *testing.T) {
	var calls atomic.Int32
	var active atomic.Int32
	releaseClose := make(chan struct{})
	var releaseOnce sync.Once
	firstCloseStarted := make(chan struct{})
	secondCloseStarted := make(chan struct{})

	transport := roundTripFunc(func(request *http.Request) (*http.Response, error) {
		callNumber := calls.Add(1)
		current := active.Add(1)
		if current > 2 {
			active.Add(-1)
			return &http.Response{
				StatusCode: http.StatusForbidden,
				Header:     make(http.Header),
				Body:       io.NopCloser(strings.NewReader("too many upstreams")),
				Request:    request,
			}, nil
		}

		return &http.Response{
			StatusCode: http.StatusPartialContent,
			Header: http.Header{
				"Content-Type":  []string{"application/octet-stream"},
				"Content-Range": []string{"bytes 0-1023/4096"},
			},
			Body: &delayedCloseBody{
				active: &active,
				closeStarted: func() chan struct{} {
					if callNumber == 1 {
						return firstCloseStarted
					}
					return secondCloseStarted
				}(),
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
	guard.leases.waitTime = 20 * time.Millisecond
	guardServer := httptest.NewServer(guard)
	defer guardServer.Close()

	key := fmt.Sprintf("%x", md5.Sum([]byte("/media/video.mkv")))
	first := openGuardStream(t, guardServer.URL, target.String(), key, "bytes=0-")
	second := openGuardStream(t, guardServer.URL, target.String(), key, "bytes=100-")
	defer func() {
		releaseOnce.Do(func() { close(releaseClose) })
		first.Body.Close()
		second.Body.Close()
	}()

	request, err := http.NewRequest(http.MethodGet, guardServer.URL+"/stream", nil)
	if err != nil {
		t.Fatal(err)
	}
	request.Header.Set(targetHeader, target.String())
	request.Header.Set(keyHeader, key)
	request.Header.Set("Range", "bytes=200-")
	third, err := http.DefaultClient.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	thirdBody, readErr := io.ReadAll(third.Body)
	third.Body.Close()
	if readErr != nil {
		t.Fatal(readErr)
	}

	if third.StatusCode != http.StatusServiceUnavailable {
		t.Fatalf("expected 503 while both upstream slots are still open, got %d: %s",
			third.StatusCode, bytes.TrimSpace(thirdBody))
	}
	if third.Header.Get("Retry-After") != "1" {
		t.Fatalf("expected a bounded retry hint, got %q", third.Header.Get("Retry-After"))
	}
	if calls.Load() != 2 {
		t.Fatalf("expected no third upstream attempt, got %d attempts", calls.Load())
	}
	select {
	case <-firstCloseStarted:
		t.Fatal("third stream forcibly closed the first healthy upstream")
	default:
	}
	select {
	case <-secondCloseStarted:
		t.Fatal("third stream forcibly closed the second healthy upstream")
	default:
	}
	if guard.timeouts.Load() != 1 || guard.breaches.Load() != 0 {
		t.Fatalf("unexpected guard counters: timeouts=%d breaches=%d",
			guard.timeouts.Load(), guard.breaches.Load())
	}
	if guard.slotWaits.Load() != 1 {
		t.Fatalf("expected one slot wait, got %d", guard.slotWaits.Load())
	}
	metrics := httptest.NewRecorder()
	guard.writeMetrics(metrics)
	if !strings.Contains(metrics.Body.String(), "emby_115_guard_slot_wait_timeouts_total 1\n") {
		t.Fatalf("slot wait timeout metric is missing: %s", metrics.Body.String())
	}

	releaseOnce.Do(func() { close(releaseClose) })
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
