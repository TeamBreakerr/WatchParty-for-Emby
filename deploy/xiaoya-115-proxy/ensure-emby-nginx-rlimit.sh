#!/bin/sh
set -eu

# Install the main-context worker descriptor limit used by the 1 MiB slice
# cache.  Xiaoya's `nginx.conf` already includes `/etc/nginx/conf.d/*.conf`
# before its `events` block, so the directive can be added without editing an
# image-owned file.  A reload is enough: Nginx applies `worker_rlimit_nofile`
# to the workers it spawns for the new configuration.

data_dir=${EMBY_115_DATA_DIR:-/data}
source_config=${EMBY_RLIMIT_SOURCE:-$data_dir/emby-nginx-rlimit.conf}
target_dir=${EMBY_RLIMIT_TARGET_DIR:-/etc/nginx/conf.d}
target_config=${EMBY_RLIMIT_TARGET:-$target_dir/emby-nginx-rlimit.conf}
main_config=${EMBY_NGINX_MAIN_CONFIG:-/etc/nginx/nginx.conf}
nginx_bin=${NGINX_BIN:-nginx}
reload=0

case "${1:-}" in
    "") ;;
    --reload) reload=1 ;;
    *)
        echo "usage: $0 [--reload]" >&2
        exit 2
        ;;
esac

if [ ! -s "$source_config" ]; then
    echo "missing $source_config" >&2
    exit 1
fi

if ! grep -Fq 'worker_rlimit_nofile' "$source_config"; then
    echo "$source_config does not declare worker_rlimit_nofile" >&2
    exit 1
fi

# The include must be evaluated in the main context.  Refuse to install rather
# than let a differently structured nginx.conf reject the directive.
if ! awk '
    /^[[:space:]]*events[[:space:]]*\{/ { exit found ? 0 : 1 }
    /^[[:space:]]*include[[:space:]]+\/etc\/nginx\/conf\.d\/\*\.conf;/ { found = 1 }
    END { exit found ? 0 : 1 }
' "$main_config"; then
    echo "$main_config does not include /etc/nginx/conf.d in the main context" >&2
    exit 1
fi

reload_nginx() {
    if [ "$reload" -eq 1 ] && [ -s /run/nginx/nginx.pid ]; then
        "$nginx_bin" -s reload
    fi
}

if [ -e "$target_config" ] && cmp -s "$source_config" "$target_config"; then
    "$nginx_bin" -t >/dev/null 2>&1
    reload_nginx
    echo "already-present"
    exit 0
fi

mkdir -p "$target_dir"

backup_config=
target_existed=0
restore_needed=0

cleanup() {
    exit_status=$?
    trap - EXIT HUP INT TERM
    set +e
    if [ "$exit_status" -ne 0 ] && [ "$restore_needed" -eq 1 ]; then
        if [ "$target_existed" -eq 1 ]; then
            cp -p "$backup_config" "$target_config"
        else
            rm -f "$target_config"
        fi
        echo "restored the previous worker descriptor limit" >&2
    fi
    [ -z "$backup_config" ] || rm -f "$backup_config"
    exit "$exit_status"
}
trap cleanup EXIT
trap 'exit 1' HUP INT TERM

if [ -e "$target_config" ]; then
    target_existed=1
    backup_config=$(mktemp)
    cp -p "$target_config" "$backup_config"
fi
restore_needed=1

cp "$source_config" "$target_config"

if ! "$nginx_bin" -t >/dev/null 2>&1; then
    echo "Nginx rejected the worker descriptor limit" >&2
    exit 1
fi

restore_needed=0
reload_nginx
echo "patched"
