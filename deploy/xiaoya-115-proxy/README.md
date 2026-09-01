# Dynamic Xiaoya 115 range proxy

This deployment layer protects Emby Range requests backed by 115 without
depending on a Chinese folder name or a fixed library path.

For each new download request under `/d/`, Nginx makes a one-byte internal probe
against OpenList. Only redirects whose final host belongs to a supported 115
download domain are proxied directly. Other providers continue through the
official Xiaoya `/d/` location.

The loopback `emby-115-guard` process keeps two upstream requests per media
path. A third Range request, which normally follows a seek or an HLS segment
rotation, waits for one of the existing requests to finish naturally. The
guard never cancels a healthy stream: doing that truncates its response and can
make Emby restart FFmpeg, creating a retry storm. If no slot is released within
the bounded wait window, the guard returns 503 without opening a third
upstream; Nginx performs one delayed, refreshed retry for either that condition
or a transient 115 403. Nginx never needs to interrupt a response that is
blocked on a slow downstream client.

The guard exposes loopback-only Prometheus counters at
`http://127.0.0.1:15678/metrics`. The integration test releases one of the two
initial streams, then asserts that the waiting third Range returns 206 without
incrementing the replacement or per-media connection-limit breach counters.
`emby_115_guard_slot_waits_total` counts requests that had to wait, and
`emby_115_guard_slot_wait_timeouts_total` counts bounded waits that could not
obtain a slot. The older replacement and replacement-timeout metrics are
retained as deprecated compatibility aliases; replacements remain zero.

Files are copied to Xiaoya's persistent `/data` mount. The installer wraps
`/updateall`, and `install-xiaoyakeeper-hook.sh` adds a post-update reinstall
command to `mycmd.txt`, so both data refreshes and container recreation restore
the includes. The installer waits for a newly recreated Nginx master and
retries reload instead of rolling back a valid overlay during the startup
window. A lightweight in-container cron check verifies both the loopback guard
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

The installer also applies a narrow compatibility patch to Xiaoya's generated
`emby.js`. Local media already goes directly to Emby's native backend. For an
internal Xiaoya `/d/` media path, the patched resolver now uses the path already
returned by `PlaybackInfo` and sends the original video request to `@backend`
without opening an extra one-byte media request. The dynamic 115 guard then
classifies and protects the actual backend read. Ready-to-use external links
retain Xiaoya's native handling, and no MP4 or other output container is
guessed. The same patch removes Xiaoya's redundant next-episode `stream.strm`
request, avoiding an invalid `.strm` FFmpeg output job.

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
2. XiaoyaKeeper's supported `mycmd.txt` post-update hook runs the installer
   immediately after `update_xiaoya`.

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
