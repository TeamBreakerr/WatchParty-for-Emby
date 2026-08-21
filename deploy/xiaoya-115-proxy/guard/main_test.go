package main

import (
	"crypto/md5"
	"crypto/tls"
	"fmt"
	"io"
	"log"
	"net/http"
	"net/http/httptest"
	"net/url"
	"sync/atomic"
	"testing"
	"time"
)

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

func TestThirdStreamCancelsOldestUpstreamBeforeConnecting(t *testing.T) {
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

	third := openGuardStream(t, guardServer.URL, upstream.URL+"/video", key, "bytes=200-299")
	thirdBody, err := io.ReadAll(third.Body)
	third.Body.Close()
	if err != nil {
		t.Fatal(err)
	}
	if third.StatusCode != http.StatusPartialContent || len(thirdBody) != 100 {
		t.Fatalf("unexpected third response: status=%d bytes=%d", third.StatusCode, len(thirdBody))
	}
	if guard.evictions.Load() != 1 {
		t.Fatalf("expected one eviction, got %d", guard.evictions.Load())
	}
	if maximum.Load() > 2 {
		t.Fatalf("opened %d concurrent upstreams", maximum.Load())
	}

	first.Body.Close()
	second.Body.Close()
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
