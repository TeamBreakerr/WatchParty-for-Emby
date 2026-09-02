#!/bin/sh
set -eu

data_dir=${EMBY_115_DATA_DIR:-/data}
default_config=${EMBY_115_DEFAULT_CONFIG:-/etc/nginx/http.d/default.conf}
emby_config=${EMBY_115_EMBY_CONFIG:-/etc/nginx/http.d/emby.conf}
runtime_config=${EMBY_115_RUNTIME_CONFIG:-/etc/nginx/http.d/emby-115-throttle.conf}
slice_cache_dir=${EMBY_115_SLICE_CACHE_DIR:-/var/cache/nginx/emby-115-slices}
slice_cache_owner=${EMBY_115_SLICE_CACHE_OWNER:-nginx:root}
nginx_bin=${EMBY_115_NGINX_BIN:-nginx}
nginx_pid_file=${EMBY_115_NGINX_PID_FILE:-/run/nginx/nginx.pid}
reload_attempts=${EMBY_115_RELOAD_ATTEMPTS:-60}
reload_delay_seconds=${EMBY_115_RELOAD_DELAY_SECONDS:-1}
updateall_path=${EMBY_115_UPDATEALL:-/updateall}
server_include='        include /data/emby-115-locations.conf;'
access_include='            include /data/emby-115-access.conf;'
wrapper_marker='codex-dynamic-emby-115-updateall-wrapper'
default_backup=
emby_config_backup=
runtime_backup=
runtime_existed=0
updateall_backup=
install_committed=0
cron_file=${EMBY_115_CRON_FILE:-/etc/crontabs/root}
cron_backup=
cron_existed=0
proxy_health_cron='* * * * * /data/ensure-emby-115-proxy.sh'
websocket_timeout_cron='* * * * * /data/ensure-emby-websocket-timeout.sh --reload'
legacy_overlay_script=${EMBY_115_LEGACY_OVERLAY:-/data/ensure-xiaoya-overlays.sh}

proxy_routes_present() {
    rendered_config=$("$nginx_bin" -T 2>&1) || return 1
    for marker in \
        '/data/emby-115-locations.conf' \
        'keys_zone=emby_115_slices:16m' \
        'proxy_cache emby_115_slices;' \
        'slice 1m;' \
        'location @emby_115_stream' \
        'location @emby_115_retry' \
        '/data/emby-115-access.conf' \
        'access_by_lua_file /data/emby-115-access.lua;'; do
        printf '%s\n' "$rendered_config" | grep -Fq "$marker" || return 1
    done
}

reload_nginx_when_ready() {
    attempt=1
    while [ "$attempt" -le "$reload_attempts" ]; do
        if [ -s "$nginx_pid_file" ]; then
            nginx_pid=$(cat "$nginx_pid_file" 2>/dev/null || true)
            case "$nginx_pid" in
                ''|*[!0-9]*) ;;
                *)
                    if kill -0 "$nginx_pid" 2>/dev/null \
                        && "$nginx_bin" -s reload; then
                        return 0
                    fi
                    ;;
            esac
        fi
        if [ "$attempt" -lt "$reload_attempts" ]; then
            sleep "$reload_delay_seconds"
        fi
        attempt=$((attempt + 1))
    done

    echo "Nginx was not ready for reload after $reload_attempts attempt(s)" >&2
    return 1
}

cleanup() {
    status=$?
    trap - EXIT HUP INT TERM
    set +e
    if [ "$status" -ne 0 ] && [ "$install_committed" -eq 0 ]; then
        if [ -n "$default_backup" ]; then
            cp -p "$default_backup" "$default_config"
        fi
        if [ -n "$emby_config_backup" ]; then
            cp -p "$emby_config_backup" "$emby_config"
        fi
        if [ "$runtime_existed" -eq 1 ] && [ -n "$runtime_backup" ]; then
            cp -p "$runtime_backup" "$runtime_config"
        elif [ "$runtime_existed" -eq 0 ]; then
            rm -f "$runtime_config"
        fi
        if [ -n "$updateall_backup" ]; then
            cp -p "$updateall_backup" "$updateall_path"
        fi
        if [ "$cron_existed" -eq 1 ] && [ -n "$cron_backup" ]; then
            cp -p "$cron_backup" "$cron_file"
        elif [ "$cron_existed" -eq 0 ] && [ -n "$cron_backup" ]; then
            rm -f "$cron_file"
        fi
        echo "restored Nginx configuration after failed 115 proxy installation" >&2
    elif [ "$status" -ne 0 ]; then
        echo "installation remains active; retry to finish reload or cleanup" >&2
    fi
    [ -z "$default_backup" ] || rm -f "$default_backup"
    [ -z "$emby_config_backup" ] || rm -f "$emby_config_backup"
    [ -z "$runtime_backup" ] || rm -f "$runtime_backup"
    [ -z "$updateall_backup" ] || rm -f "$updateall_backup"
    [ -z "$cron_backup" ] || rm -f "$cron_backup"
    exit "$status"
}
trap cleanup EXIT
trap 'exit 1' HUP INT TERM

required_files='emby-115-access.conf
emby-115-access.lua
emby-115-locations.conf
emby-115-retry.lua
emby-115-throttle.conf
emby-115-guard
emby_115_policy.lua
ensure-emby-115-guard.sh
ensure-emby-115-proxy.sh
install-emby-115-runtime.sh
install-emby-115-proxy-after-start.sh
emby-websocket-diagnostic.conf
emby-websocket-timeout.conf
ensure-emby-websocket-timeout.sh
emby-web-cache-buster.conf
ensure-emby-web-cache-buster.sh
ensure-emby-direct-link-fallback.sh
updateall-emby-115-wrapper.sh'
for required_file in $required_files; do
    if [ ! -s "$data_dir/$required_file" ]; then
        echo "missing $data_dir/$required_file" >&2
        exit 1
    fi
done

if [ ! -x "$data_dir/emby-115-guard" ] \
    || [ ! -x "$data_dir/ensure-emby-115-guard.sh" ] \
    || [ ! -x "$data_dir/ensure-emby-115-proxy.sh" ] \
    || [ ! -x "$data_dir/install-emby-115-runtime.sh" ] \
    || [ ! -x "$data_dir/install-emby-115-proxy-after-start.sh" ] \
    || [ ! -x "$data_dir/ensure-emby-websocket-timeout.sh" ] \
    || [ ! -x "$data_dir/ensure-emby-web-cache-buster.sh" ] \
    || [ ! -x "$data_dir/ensure-emby-direct-link-fallback.sh" ]; then
    echo "115 guard executables are not executable" >&2
    exit 1
fi

mkdir -p "$data_dir/logs"
mkdir -p "$slice_cache_dir"
chown "$slice_cache_owner" "$slice_cache_dir"
chmod 0750 "$slice_cache_dir"

if ! grep -Fq 'location /d/ {' "$default_config"; then
    echo "could not find the official /d/ location in $default_config" >&2
    exit 1
fi

default_backup=$(mktemp)
cp -p "$default_config" "$default_backup"
emby_config_backup=$(mktemp)
cp -p "$emby_config" "$emby_config_backup"
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

cron_backup=$(mktemp)
if [ -e "$cron_file" ]; then
    cron_existed=1
    cp -p "$cron_file" "$cron_backup"
else
    : >"$cron_backup"
    : >"$cron_file"
fi
if grep -Fq '/data/ensure-emby-115-guard.sh' "$cron_file"; then
    sed -i '\|/data/ensure-emby-115-guard.sh|d' "$cron_file"
fi
if ! grep -Fq '/data/ensure-emby-115-proxy.sh' "$cron_file"; then
    printf '%s\n' "$proxy_health_cron" >>"$cron_file"
fi
if ! grep -Fq '/data/ensure-emby-websocket-timeout.sh' "$cron_file"; then
    printf '%s\n' "$websocket_timeout_cron" >>"$cron_file"
fi
"$data_dir/ensure-emby-115-guard.sh"
"$data_dir/ensure-emby-websocket-timeout.sh"
"$data_dir/ensure-emby-web-cache-buster.sh"
"$data_dir/ensure-emby-direct-link-fallback.sh"

"$nginx_bin" -t
if ! proxy_routes_present; then
    echo "Nginx configuration is missing one or more 115 proxy routes" >&2
    exit 1
fi

if ! grep -Fq "$wrapper_marker" "$updateall_path"; then
    updateall_backup=$(mktemp)
    cp -p "$updateall_path" "$updateall_backup"
    cp -p "$data_dir/updateall-emby-115-wrapper.sh" "$updateall_path.codex-new"
    chmod 755 "$updateall_path.codex-new"
    if [ ! -e "$updateall_path.xiaoya-original" ]; then
        mv "$updateall_path" "$updateall_path.xiaoya-original"
    fi
    mv "$updateall_path.codex-new" "$updateall_path"
fi

# At this point all on-disk changes have passed nginx -t and nginx -T. Keep
# them installed if the freshly recreated container is not ready to reload
# yet; the minute health check can safely retry the reload later.
install_committed=1

if [ "${1:-}" = "--reload" ]; then
    reload_nginx_when_ready
fi

# Retire the previous full-overlay minute cron only after this installation has
# validated and, when requested, reloaded successfully.
if grep -Fq "$legacy_overlay_script" "$cron_file"; then
    sed -i "\|$legacy_overlay_script|d" "$cron_file"
fi
rm -f "$legacy_overlay_script"
