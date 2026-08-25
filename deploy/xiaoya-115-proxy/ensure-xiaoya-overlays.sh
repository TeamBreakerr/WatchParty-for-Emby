#!/bin/sh
set -eu

default_config=/etc/nginx/http.d/default.conf
emby_config=/etc/nginx/http.d/emby.conf
runtime_throttle=/etc/nginx/http.d/emby-115-throttle.conf
installer=/data/install-emby-115-proxy.sh

needs_install=0
if [ ! -r "$default_config" ] \
    || [ ! -r "$emby_config" ] \
    || [ ! -x "$installer" ]; then
    echo "Xiaoya overlay prerequisites are not ready" >&2
    exit 1
fi

if ! grep -Fq '/data/emby-115-locations.conf' "$default_config" \
    || ! grep -Fq '/data/emby-115-access.conf' "$default_config" \
    || ! grep -Fq '/data/emby-websocket-timeout.conf' "$emby_config" \
    || ! cmp -s /data/emby-115-throttle.conf "$runtime_throttle" \
    || ! grep -Fq 'codex-dynamic-emby-115-updateall-wrapper' /updateall; then
    needs_install=1
fi

if [ "$needs_install" -eq 1 ]; then
    exec /data/install-emby-115-proxy.sh --reload
fi

/data/ensure-emby-115-guard.sh
exec /data/ensure-emby-websocket-timeout.sh --reload
