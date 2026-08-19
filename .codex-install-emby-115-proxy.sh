#!/bin/sh
set -eu

default_config=/etc/nginx/http.d/default.conf
emby_config=/etc/nginx/http.d/emby.conf
runtime_config=/etc/nginx/http.d/emby-115-throttle.conf
include_line='        include /data/emby-115-locations.conf;'
websocket_include_line='        include /data/emby-websocket-proxy.conf;'
websocket_reconnect_include_line='    include /data/emby-websocket-reconnect.conf;'
web_cache_buster_include_line='        include /data/emby-web-cache-buster.conf;'
wrapper_marker='codex-emby-115-updateall-wrapper'
emby_config_backup=

cleanup() {
    status=$?
    remove_backup=1
    trap - EXIT HUP INT TERM
    set +e
    if [ "$status" -ne 0 ] && [ -n "$emby_config_backup" ]; then
        if cp -p "$emby_config_backup" "$emby_config"; then
            echo "restored $emby_config after failed Nginx update" >&2
        else
            remove_backup=0
            echo "could not restore $emby_config; backup preserved at $emby_config_backup" >&2
        fi
    fi
    if [ -n "$emby_config_backup" ] && [ "$remove_backup" -eq 1 ]; then
        rm -f "$emby_config_backup"
    fi
    exit "$status"
}
trap cleanup EXIT
trap 'exit 1' HUP INT TERM

if ! /bin/grep -Fq '/data/emby-115-locations.conf' "$default_config"; then
    sed -i "/^[[:space:]]*location \/d\/ {/i\\$include_line" "$default_config"
fi

cp -p /data/emby-115-throttle.conf "$runtime_config"

if [ ! -s /data/emby-websocket-proxy.conf ]; then
    echo "missing /data/emby-websocket-proxy.conf" >&2
    exit 1
fi

if [ ! -s /data/emby-websocket-reconnect.conf ]; then
    echo "missing /data/emby-websocket-reconnect.conf" >&2
    exit 1
fi

if [ ! -s /data/emby-web-cache-buster.conf ]; then
    echo "missing /data/emby-web-cache-buster.conf" >&2
    exit 1
fi

# Emby appends ?v=<data-appversion> to every dashboard module and advertises
# that URL as cacheable for a year.  Include a small body-filter/cache-header
# patch in both 2345 and 2347 index locations so a dashboard generation change
# cannot be hidden by an old browser or proxy entry.  The include is
# idempotent and lives under /data, which survives Xiaoya container rebuilds.
if ! /bin/grep -Fq '/data/emby-web-cache-buster.conf' "$emby_config"; then
    sed -i "/^[[:space:]]*location ~\\* \/web\/index\\.html {/a\\${web_cache_buster_include_line}" "$emby_config"
fi

needs_reconnect_include=0
needs_websocket_include=0
if ! /bin/grep -Fq '/data/emby-websocket-reconnect.conf' "$emby_config"; then
    needs_reconnect_include=1
fi
if ! /bin/grep -Fq '/data/emby-websocket-proxy.conf' "$emby_config"; then
    needs_websocket_include=1
fi

if [ "$needs_reconnect_include" -eq 1 ] || [ "$needs_websocket_include" -eq 1 ]; then
    websocket_location_line=$(
        /bin/grep -n -m1 -F 'location ~ /(socket|embywebsocket) {' "$emby_config" \
            | cut -d: -f1
    )
    if [ -z "$websocket_location_line" ]; then
        echo "could not find Emby websocket location in $emby_config" >&2
        exit 1
    fi
    backup_candidate=$(mktemp)
    if ! cp -p "$emby_config" "$backup_candidate"; then
        rm -f "$backup_candidate"
        echo "could not back up $emby_config" >&2
        exit 1
    fi
    emby_config_backup=$backup_candidate
fi

if [ "$needs_reconnect_include" -eq 1 ]; then
    sed -i "${websocket_location_line}i\\${websocket_reconnect_include_line}" "$emby_config"
    websocket_location_line=$((websocket_location_line + 1))
fi

if [ "$needs_websocket_include" -eq 1 ]; then
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
