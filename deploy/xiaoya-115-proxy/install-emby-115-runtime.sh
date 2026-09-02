#!/bin/sh
set -eu

data_dir=${EMBY_115_DATA_DIR:-/data}
default_config=${EMBY_115_DEFAULT_CONFIG:-/etc/nginx/http.d/default.conf}
runtime_config=${EMBY_115_RUNTIME_CONFIG:-/etc/nginx/http.d/emby-115-throttle.conf}
nginx_bin=${EMBY_115_NGINX_BIN:-nginx}
server_include='        include /data/emby-115-locations.conf;'
access_include='            include /data/emby-115-access.conf;'
default_backup=
runtime_backup=
runtime_existed=0
install_committed=0

cleanup() {
    status=$?
    trap - EXIT HUP INT TERM
    set +e
    if [ "$status" -ne 0 ] && [ "$install_committed" -eq 0 ]; then
        [ -z "$default_backup" ] || cp -p "$default_backup" "$default_config"
        if [ "$runtime_existed" -eq 1 ] && [ -n "$runtime_backup" ]; then
            cp -p "$runtime_backup" "$runtime_config"
        elif [ "$runtime_existed" -eq 0 ]; then
            rm -f "$runtime_config"
        fi
        echo "restored Nginx configuration after failed runtime installation" >&2
    fi
    [ -z "$default_backup" ] || rm -f "$default_backup"
    [ -z "$runtime_backup" ] || rm -f "$runtime_backup"
    exit "$status"
}
trap cleanup EXIT
trap 'exit 1' HUP INT TERM

case "${1:-}" in
    ''|--reload) ;;
    *) echo "usage: $0 [--reload]" >&2; exit 2 ;;
esac

for required_file in \
    emby-115-access.conf \
    emby-115-access.lua \
    emby-115-locations.conf \
    emby-115-retry.lua \
    emby-115-throttle.conf \
    emby-115-guard \
    emby_115_policy.lua \
    ensure-emby-115-guard.sh; do
    if [ ! -s "$data_dir/$required_file" ]; then
        echo "missing $data_dir/$required_file" >&2
        exit 1
    fi
done

if [ ! -x "$data_dir/emby-115-guard" ] \
    || [ ! -x "$data_dir/ensure-emby-115-guard.sh" ]; then
    echo "115 guard executables are not executable" >&2
    exit 1
fi
if ! grep -Fq 'location /d/ {' "$default_config"; then
    echo "could not find the official /d/ location in $default_config" >&2
    exit 1
fi

default_backup=$(mktemp)
cp -p "$default_config" "$default_backup"
if [ -e "$runtime_config" ]; then
    runtime_existed=1
    runtime_backup=$(mktemp)
    cp -p "$runtime_config" "$runtime_backup"
fi

if ! grep -Fq '/data/emby-115-locations.conf' "$default_config"; then
    sed -i "/^[[:space:]]*location \/d\/ {/i\\$server_include" "$default_config"
fi
if ! grep -Fq '/data/emby-115-access.conf' "$default_config"; then
    sed -i "/^[[:space:]]*location \/d\/ {/a\\$access_include" "$default_config"
fi
cp -p "$data_dir/emby-115-throttle.conf" "$runtime_config"

"$data_dir/ensure-emby-115-guard.sh"
"$nginx_bin" -t
rendered_config=$("$nginx_bin" -T 2>&1)
for marker in \
    '/data/emby-115-locations.conf' \
    'location @emby_115_stream' \
    'location @emby_115_retry' \
    '/data/emby-115-access.conf' \
    'access_by_lua_file /data/emby-115-access.lua;'; do
    printf '%s\n' "$rendered_config" | grep -Fq "$marker" || {
        echo "runtime Nginx configuration is missing $marker" >&2
        exit 1
    }
done

install_committed=1
if [ "${1:-}" = "--reload" ]; then
    "$nginx_bin" -s reload
fi
