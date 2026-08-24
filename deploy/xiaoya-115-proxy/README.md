# Dynamic Xiaoya 115 range proxy

This deployment layer protects Emby Range requests backed by 115 without
depending on a Chinese folder name or a fixed library path.

For each new download request under `/d/`, Nginx makes a one-byte internal probe
against OpenList. Only redirects whose final host belongs to a supported 115
download domain are proxied directly. Other providers continue through the
official Xiaoya `/d/` location.

The loopback `emby-115-guard` process keeps two upstream requests per media
path. A third Range request, which normally follows a seek, cancels the oldest
upstream HTTP context, waits for its CDN socket to close, and allows a short
teardown grace period before opening the new request. If the old connection
does not close by the deadline, the guard returns 503 without opening a third
upstream; Nginx performs one delayed, refreshed retry for either that condition
or a transient 115 403. Nginx never needs to interrupt a response that is
blocked on a slow downstream client.

The guard exposes loopback-only Prometheus counters at
`http://127.0.0.1:15678/metrics`. The integration test asserts that a seek
increments the replacement counter, completes within three seconds, and does
not increment the per-media connection-limit breach counter.

Files are copied to Xiaoya's persistent `/data` mount. The installer wraps
`/updateall`, and `install-xiaoyakeeper-hook.sh` adds a post-update reinstall
command to `mycmd.txt`, so both data refreshes and container recreation restore
the includes. A root cron health check restarts the loopback guard after an
ordinary restart of the existing container.

The same installer also repairs Xiaoya's inner `/etc/nginx/http.d/emby.conf`.
Its stock `listen 2345` server sets `proxy_read_timeout 20s`, and the stock
`/socket`/`/embywebsocket` locations inherit that value. The persistent
`emby-websocket-timeout.conf` include raises the WebSocket read/send timeout,
disables proxy buffering, and enables TCP keepalive in both locations. The
idempotent `ensure-emby-websocket-timeout.sh` script validates the locations,
backs up the file, runs `nginx -t`, and restores the backup if validation fails.
The installer calls it after updates and adds a once-per-minute self-healing
cron entry, so a regenerated inner config is repaired without changing Emby's
client protocol or injecting an application-level KeepAlive message.

Build the guard for this ARM64 Xiaoya host before installation:

```sh
./build-guard.sh arm64 /tmp/emby-115-guard
```

Copy the resulting binary and the deployment files to Xiaoya's persistent
`/data`, then run `install-emby-115-proxy.sh --reload` inside the container.
