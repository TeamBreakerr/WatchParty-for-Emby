#!/bin/sh
set -eu

nginx_bin=${EMBY_115_NGINX_BIN:-nginx}
guard_ensure=${EMBY_115_GUARD_ENSURE:-/data/ensure-emby-115-guard.sh}
installer=${EMBY_115_INSTALLER:-/data/install-emby-115-proxy.sh}
lock_dir=${EMBY_115_HEALTH_LOCK_DIR:-/tmp/emby-115-proxy-health.lock}

proxy_routes_present() {
    rendered_config=$("$nginx_bin" -T 2>&1) || return 1
    for marker in \
        '/data/emby-115-locations.conf' \
        'location @emby_115_stream' \
        'location @emby_115_retry' \
        '/data/emby-115-access.conf' \
        'access_by_lua_file /data/emby-115-access.lua;'; do
        printf '%s\n' "$rendered_config" | grep -Fq "$marker" || return 1
    done
}

"$guard_ensure"
if proxy_routes_present; then
    exit 0
fi

if ! mkdir "$lock_dir" 2>/dev/null; then
    exit 0
fi
trap 'rmdir "$lock_dir" 2>/dev/null || true' EXIT HUP INT TERM

# Another overlapping check may have repaired the routes while this process
# waited for the lock.
if proxy_routes_present; then
    exit 0
fi

echo "115 proxy routes are incomplete; reinstalling the persistent overlay" >&2
"$installer" --reload

if ! proxy_routes_present; then
    echo "115 proxy routes remain incomplete after reinstall" >&2
    exit 1
fi
