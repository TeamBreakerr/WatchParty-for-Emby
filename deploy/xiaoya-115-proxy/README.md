# Dynamic Xiaoya 115 range proxy

This deployment layer protects Emby Range requests backed by 115 without
depending on a Chinese folder name or a fixed library path.

For each new download request under `/d/`, Nginx makes a one-byte internal probe
against OpenList. Only redirects whose final host belongs to a supported 115
download domain are proxied directly. Other providers continue through the
official Xiaoya `/d/` location.

The loopback `emby-115-guard` process keeps two upstream requests per media
path. A third Range request, which normally follows a seek, cancels the oldest
upstream HTTP context and waits for its CDN socket to close before opening the
new request. One refreshed retry is allowed when 115 still returns a transient
403. Nginx never needs to interrupt a response that is blocked on a slow
downstream client.

Files are copied to Xiaoya's persistent `/data` mount. The installer wraps
`/updateall`, and `install-xiaoyakeeper-hook.sh` adds a post-update reinstall
command to `mycmd.txt`, so both data refreshes and container recreation restore
the includes. A root cron health check restarts the loopback guard after an
ordinary restart of the existing container.

Build the guard for this ARM64 Xiaoya host before installation:

```sh
./build-guard.sh arm64 /tmp/emby-115-guard
```

Copy the resulting binary and the deployment files to Xiaoya's persistent
`/data`, then run `install-emby-115-proxy.sh --reload` inside the container.
