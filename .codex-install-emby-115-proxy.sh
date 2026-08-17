#!/bin/sh
set -eu

default_config=/etc/nginx/http.d/default.conf
emby_config=/etc/nginx/http.d/emby.conf
runtime_config=/etc/nginx/http.d/emby-115-throttle.conf
include_line='        include /data/emby-115-locations.conf;'
websocket_include_line='        include /data/emby-websocket-proxy.conf;'
wrapper_marker='codex-emby-115-updateall-wrapper'

if ! /bin/grep -Fq '/data/emby-115-locations.conf' "$default_config"; then
    sed -i "/^[[:space:]]*location \/d\/ {/i\\$include_line" "$default_config"
fi

cp -p /data/emby-115-throttle.conf "$runtime_config"

if [ ! -s /data/emby-websocket-proxy.conf ]; then
    echo "missing /data/emby-websocket-proxy.conf" >&2
    exit 1
fi

if ! /bin/grep -Fq '/data/emby-websocket-proxy.conf' "$emby_config"; then
    websocket_location_line=$(
        /bin/grep -n -m1 -F 'location ~ /(socket|embywebsocket) {' "$emby_config" \
            | cut -d: -f1
    )
    if [ -z "$websocket_location_line" ]; then
        echo "could not find Emby websocket location in $emby_config" >&2
        exit 1
    fi
    sed -i "${websocket_location_line}a\\${websocket_include_line}" "$emby_config"
fi

if ! /bin/grep -Fq "$wrapper_marker" /updateall; then
    cp -p /data/updateall-emby-115-wrapper.sh /updateall.codex-new
    chmod 755 /updateall.codex-new
    if [ ! -e /updateall.xiaoya-original ]; then
        mv /updateall /updateall.xiaoya-original
    fi
    mv /updateall.codex-new /updateall
fi

nginx -t

if [ "${1:-}" = "--reload" ]; then
    attempts=0
    while [ "$attempts" -lt 60 ]; do
        if [ -s /run/nginx/nginx.pid ]; then
            nginx_pid=$(cat /run/nginx/nginx.pid)
            if [ -d "/proc/$nginx_pid" ]; then
                nginx -s reload
                exit 0
            fi
        fi
        attempts=$((attempts + 1))
        sleep 1
    done
    echo "nginx did not become ready within 60 seconds" >&2
    exit 1
fi
