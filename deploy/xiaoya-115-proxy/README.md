# Dynamic Xiaoya 115 range proxy

This deployment layer protects Emby Range requests backed by 115 without
depending on a Chinese folder name or a fixed library path.

Physical local files stay on Emby's native path and never enter this layer. For
each Xiaoya download request under `/d/`, Nginx makes a one-byte internal probe
against OpenList. The probe does not read the remote CDN body: OpenList answers
with the current signed redirect, which is used to identify the provider and
refresh an expiring URL. Only redirects whose final host belongs to a supported
115 download domain are proxied directly. Other providers continue through the
official Xiaoya `/d/` location. A `.strm` suffix can identify the original item
as remote, but it does not contain the final signed CDN URL or distinguish 115
from another provider by itself.

115 signs each download URL for the User-Agent that requested it. Emby's
progressive `VideoService` fetches remote media without a User-Agent, while the
Go HTTP transport otherwise supplies its own default on the subsequent CDN
request. That mismatch makes a successfully resolved URL return 403. The
resolver, Nginx stream locations, and guard therefore use the same non-empty
`Emby-Xiaoya-Proxy/1.0` User-Agent for the complete signed-link lifecycle.

Nginx converts every protected Range response into 1 MiB cacheable slices. A
slow Emby/FFmpeg reader can keep its downstream response open, while each 115
upstream request finishes as soon as its small slice has been downloaded. A
privacy-safe cache key combines the hash of the stable media path with the byte
range; signed URLs, tokens, client addresses, and request headers never enter
the key. Cache locking also collapses simultaneous reads of the same slice.
The guard can wait up to 5 seconds for a lease and 250 milliseconds for close
grace, then gives the complete 1 MiB upstream request 60 seconds. Nginx holds
the per-slice cache lock for 70 seconds, leaving an explicit margin so a slow
fill finishes or is cancelled before another request for that slice can reach
the upstream.

The loopback `emby-115-guard` process still permits at most two upstream
requests per media path, but now leases represent short slices instead of
whole playback streams. Additional slices wait in FIFO order for a natural
release. The guard never cancels a healthy request: doing so truncates an HLS
input and can make Emby restart FFmpeg, creating a retry storm. If no slice
slot is released within the bounded wait window, the guard returns 503 without
opening another upstream; Nginx performs one delayed, refreshed retry for that
condition or a transient 115 403. Physical local files never enter this path.

The guard exposes loopback-only Prometheus counters at
`http://127.0.0.1:15678/metrics`. The integration test keeps two slow
downstream readers open, then asserts that a third seek Range returns 206 by
rotating through short upstream slices without terminating either reader or
incrementing the replacement, wait-timeout, or per-media connection-limit
breach counters.
`emby_115_guard_slot_waits_total` counts requests that had to wait, and
`emby_115_guard_slot_wait_timeouts_total` counts bounded waits that could not
obtain a slot. The older replacement and replacement-timeout metrics are
retained as deprecated compatibility aliases; replacements remain zero.
`test/xiaoyaSliceSingleFlight.sh` runs against Xiaoya's own Nginx with a unique
empty cache and a controlled two-second upstream. It requires the overlapping
requests to finish as `MISS` then `HIT` while the mock receives exactly one
request, so a pre-existing cache entry or a fast sequential fill cannot make
the single-flight check pass accidentally.

Files are copied to Xiaoya's persistent `/data` mount. The installer wraps
`/updateall`, and `install-xiaoyakeeper-hook.sh` adds a post-update reinstall
command to `mycmd.txt`, so both data refreshes and container recreation restore
the includes. The post-update command first waits for the new Nginx master,
official `/d/` configuration, and OpenList listener to become ready, then runs
the installer once. It therefore never sends reload to the stale PID left
during container replacement. The installer also preserves a validated overlay
if a later reload race occurs. A lightweight in-container cron check verifies
both the loopback guard
and the effective `nginx -T` route set; if the guard is healthy but the `/d/`
access hook or named stream/retry locations are missing, it reruns the complete
installer. No host systemd timer is required.

The same installer also repairs Xiaoya's inner `/etc/nginx/http.d/emby.conf`.
Its stock `listen 2345` server sets `proxy_read_timeout 20s`, and the stock
`/socket`/`/embywebsocket` locations inherit that value. The persistent
`emby-websocket-timeout.conf` include raises the WebSocket read/send timeout to
24 hours, disables proxy buffering, and enables TCP keepalive in both
locations. The idempotent `ensure-emby-websocket-timeout.sh` script validates
the locations,
installs a privacy-safe diagnostic format, backs up both runtime files, runs
`nginx -t`, and restores the backups if validation fails. The diagnostic log
contains only a timestamp, proxy layer, Nginx connection sequence, status,
duration, completion state, upstream timing, byte counts and a transport-boundary
hint. A duration in the narrow 24-hour timeout window is explicitly classified
as an Nginx idle timeout even though upgraded tunnels retain HTTP status 101.
The log deliberately excludes addresses, request paths, query strings,
headers, cookies, user agents and tokens.

Routing follows what the client asked for, never who is watching. An earlier
revision asked the authenticated `WatchParty/List` endpoint on every playback
request and kept an active room's media on Emby's `@backend` "so the 115 guard
can protect the read", which created the very server-side read it then
protected. It also rested on a wrong premise. Measured against the provider, the
limit is not on concurrent connections: reads spaced a few hundred milliseconds
apart all succeed and several slow readers can hold one file open at once, while
a third read that *starts together* with two others is rejected. Two
participants therefore direct-play the same file perfectly, and a larger party
is kept inside that burst by spacing the server-issued commands rather than by
moving the bytes through Emby.

`ensure-emby-room-agnostic-routing.sh` removes the room-aware patch and
validates that nothing reintroduces it: no `WatchParty/List` lookup, no
membership test, and Xiaoya's own `stream.strm` probe, cached direct-link
resolution, helper handling and next-item preload all intact. Local media stays
on the native Emby backend, and no MP4 or other output container is guessed. The
plugin's `ProviderBurstPacer` owns the other half: participant commands that make
a client open a new provider read (`PlayNow`, `Resume`, `Seek`) are released two
per 400 ms window per party, so a party of two never waits while a third
participant is delayed by one window instead of receiving a 403 that a
direct-playing client cannot retry. `Pause` and `Stop` end a read rather than
starting one and are never delayed.

Both `emby.js` and `emby.conf` now use Docker DNS (`emby:6908`) for the Emby
upstream. A literal container address becomes stale when Emby is recreated: the
main proxy can still work while njs metadata lookups fail and turn original or
direct-stream requests into HTTP 500. `ensure-emby-docker-upstream.sh` repairs
the two generated files transactionally and restores both if `nginx -t` fails.

The upstream name alone is not enough. njs resolves an `ngx.fetch` host name
through Nginx's own `resolver` directive, never through `/etc/resolv.conf`, and
Xiaoya's generated `emby.conf` declares only public resolvers, which cannot
answer for a Docker container name. The same repair therefore installs Docker's
embedded DNS as a server-scope resolver in every Emby server block, leaving the
http-scope public resolver in place for everything else. A server block that
already declares its own resolver is left alone rather than given a duplicate.
Without this the njs metadata lookups fail on a regenerated `emby.conf` and
every direct-play client receives HTTP 500 with `error: emby_api fetch failed`.

Ordinary playback resolves its signed direct link against OpenList's own
listener rather than Xiaoya's `/d/` location. The two are no longer
interchangeable: this layer installs an access hook on `/d/`, so that location
answers a protected read with the media body in 1 MiB slices instead of
OpenList's 302. njs caps `ngx.fetch` at `max_response_body_size`, so resolving
through `/d/` makes a multi-gigabyte body raise an exception, `fetchXYApi`
returns `error: xy_api fetch failed`, and every direct-play client receives
HTTP 500 while Emby Web silently falls back to a server transcode.
`ensure-emby-openlist-resolver.sh` rewrites the `alistFilePath` and
`alistNextPath` origins to OpenList and validates that no direct-link
resolution addresses the guarded location. Link resolution therefore stays
metadata-only, and the guard keeps protecting the reads that Emby itself
performs for active room media.

Room membership is not the only boundary. Xiaoya routes `/videos/*/master` and
`/videos/*/live` through njs next to `/videos/*/original` and
`/videos/*/stream`, and answers all of them with a direct-link 302. Those two
locations carry `.m3u8` manifests: the client is asking the server to produce a
stream, and a redirect to the original file on the provider's CDN can never
satisfy it. Emby Web requests `master.m3u8` whenever it has to transcode - HEVC
it cannot decode, or an ASS subtitle it needs burned in - so a redirected
manifest makes its player fail and retry, which is how one page can leave
several transcode sessions behind. `ensure-emby-manifest-backend.sh` keeps
every manifest on Emby's backend, in or out of a room, and leaves requests for
the original file on Xiaoya's native direct-link path. The rule is the request
class, not the room: ask for the original file and the client gets a direct
link; ask the server to render a stream and Emby renders it.

A placeholder resolution is a failure, not media. When Xiaoya cannot produce a
real link - an expired share, a failed `AliyundriveShare2Pan115` transfer,
provider rate limiting - its `/d/` handler answers `302` to
`img.xiaoya.pro/abnormal.png`. That is a successful redirect carrying an image,
so nothing downstream recognises it: the link cache stores it for its full
lifetime and the client is handed a PNG in place of the video, which then keeps
failing for hours after the provider recovers.
`ensure-emby-placeholder-guard.sh` keeps a placeholder out of the cache and
answers the request with `502` instead of a redirect no player can use.

Nginx's worker descriptor limit is raised for the same slice cache. Each slice
needs its own cache and temporary descriptor, so one multi-gigabyte playback
exhausts Xiaoya's stock soft `RLIMIT_NOFILE` of 1024, logs
`open() ... failed (24: No file descriptors available)` and truncates the
stream. `emby-nginx-rlimit.conf` carries `worker_rlimit_nofile`, and
`ensure-emby-nginx-rlimit.sh` installs it through the `/etc/nginx/conf.d`
include that Xiaoya's `nginx.conf` already evaluates in the main context, so no
image-owned file is edited. The installer refuses to write the file if that
include is missing or is not in the main context. A reload is enough: Nginx
applies the limit to the workers it spawns for the new configuration.

The Emby Web seek patch changes only explicit seek reporting. Older revisions
also changed global STRM output selection and retried a failed HLS `play()`;
the current patcher migrates those edits back to Emby's native implementation.
This keeps ordinary playback and cancellation semantics outside a room
untouched. The installer also performs a one-frame ASS render once
per Emby container start. This warms libass and the configured fallback font
before a user requests a subtitle-burned HLS stream. It avoids spending almost
the entire first-segment deadline scanning fonts, and deliberately does not
create another playback request or Watch Party generation.

The log's `termination_hint` is evidence about the boundary Nginx observed. A
downstream close can mean the iOS app/device or the network path, and a normal
WebSocket close handshake can end cleanly at both peers; the log does not claim
an identity that Nginx cannot observe. Comparing the outer Nginx Proxy Manager
event with the inner Xiaoya event separates downstream/network failures from
inner Nginx timeouts and Emby-upstream failures.

The outer Nginx Proxy Manager has a native persistent override point:
`/data/nginx/custom/http_top.conf`. Install
`emby-websocket-diagnostic.conf` there, and keep the host-specific WebSocket
location in NPM's database-backed Advanced configuration. Both live under
NPM's `/data` mount and are independent of Xiaoya container updates.

Xiaoya's image does not automatically include an Nginx file from `/data`.
`/etc/nginx/http.d` belongs to the image and is replaced when the container is
recreated. Persistence is therefore provided at two levels:

1. all overlay sources live in Xiaoya's persistent `/data` bind mount;
2. XiaoyaKeeper's supported `mycmd.txt` post-update hook runs the narrow
   `install-emby-115-runtime.sh` installer immediately after `update_xiaoya`.

The post-update installer restores the 115 route, its loopback guard, the
Docker-DNS Emby upstream, the room-agnostic njs routing, the OpenList
direct-link resolution, the manifest routing and the worker descriptor limit in
the regenerated runtime files, and the Nginx reload. It does not install a periodic cron, wrap
`/updateall`, or touch the WebSocket configuration.

The installer also retains the existing in-container lightweight checks for the
115 guard, effective proxy routes, and WebSocket include. None of these
mechanisms changes Emby's client protocol or injects an application-level
KeepAlive message.

Build the guard for this ARM64 Xiaoya host before installation:

```sh
./build-guard.sh arm64 /tmp/emby-115-guard
```

Copy the resulting binary and the deployment files to Xiaoya's persistent
`/data`, then run `install-emby-115-proxy.sh --reload` inside the container.
The installer also removes the retired `/data/ensure-xiaoya-overlays.sh`
minute-cron entry and script after a successful upgrade.

Hosts that previously installed `watchparty-xiaoya-overlay.timer` need this
one-time host cleanup because a container cannot manage its host's systemd:

```sh
sudo systemctl disable --now watchparty-xiaoya-overlay.timer
sudo systemctl stop watchparty-xiaoya-overlay.service
sudo rm -f /etc/systemd/system/watchparty-xiaoya-overlay.timer
sudo rm -f /etc/systemd/system/watchparty-xiaoya-overlay.service
sudo systemctl daemon-reload
```
