#!/bin/sh
set -eu

nginx_pid_file=${EMBY_115_NGINX_PID_FILE:-/run/nginx/nginx.pid}
proc_root=${EMBY_115_PROC_ROOT:-/proc}
default_config=${EMBY_115_DEFAULT_CONFIG:-/etc/nginx/http.d/default.conf}
curl_bin=${EMBY_115_CURL_BIN:-curl}
openlist_url=${EMBY_115_OPENLIST_URL:-http://127.0.0.1:5244/}
installer=${EMBY_115_INSTALLER:-/data/install-emby-115-runtime.sh}
wait_attempts=${EMBY_115_STARTUP_WAIT_ATTEMPTS:-300}
wait_delay_seconds=${EMBY_115_STARTUP_WAIT_DELAY_SECONDS:-1}

services_ready() {
    [ -s "$default_config" ] \
        && grep -Fq 'location /d/ {' "$default_config" \
        || return 1
    [ -s "$nginx_pid_file" ] || return 1

    nginx_pid=$(cat "$nginx_pid_file" 2>/dev/null || true)
    case "$nginx_pid" in
        ''|*[!0-9]*) return 1 ;;
    esac
    [ -r "$proc_root/$nginx_pid/comm" ] || return 1
    nginx_process=$(tr -d '\r\n' <"$proc_root/$nginx_pid/comm")
    case "$nginx_process" in
        nginx*) ;;
        *) return 1 ;;
    esac

    "$curl_bin" -sS -o /dev/null --max-time 1 "$openlist_url"
}

attempt=1
while [ "$attempt" -le "$wait_attempts" ]; do
    if services_ready; then
        exec "$installer" --reload
    fi
    if [ "$attempt" -lt "$wait_attempts" ]; then
        sleep "$wait_delay_seconds"
    fi
    attempt=$((attempt + 1))
done

echo "Xiaoya services did not become ready within $wait_attempts check(s)" >&2
exit 1
