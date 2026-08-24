#!/bin/sh
set -eu

config=${EMBY_NGINX_CONFIG:-/etc/nginx/http.d/emby.conf}
include_file=${EMBY_WEBSOCKET_INCLUDE:-/data/emby-websocket-timeout.conf}
include_token=$include_file
include_line="        include $include_file;"
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

if [ ! -r "$config" ]; then
    echo "missing Nginx config: $config" >&2
    exit 1
fi
if [ ! -s "$include_file" ]; then
    echo "missing WebSocket timeout include: $include_file" >&2
    exit 1
fi

location_token='/(socket|embywebsocket)'
if ! grep -Fq "$location_token" "$config"; then
    echo "could not find an Emby WebSocket location in $config" >&2
    exit 1
fi

validate_config() {
    awk -v include_token="$include_token" -v location_token="$location_token" '
        BEGIN {
            in_block = 0
            block_count = 0
            missing_count = 0
        }
        {
            if (!in_block && index($0, "location") > 0 && index($0, location_token) > 0 && index($0, "{") > 0) {
                in_block = 1
                has_include = 0
                block_count++
                next
            }
            if (in_block && index($0, include_token) > 0) {
                has_include = 1
            }
            if (in_block && $0 ~ /^[[:space:]]*}/) {
                if (!has_include) {
                    missing_count++
                }
                in_block = 0
            }
        }
        END {
            if (in_block || block_count == 0 || missing_count != 0) {
                exit 1
            }
        }
    ' "$1"
}

if validate_config "$config"; then
    if [ "$reload" -eq 1 ]; then
        "$nginx_bin" -t >/dev/null 2>&1
    fi
    exit 0
fi

temporary_config=$(mktemp)
backup_config=$(mktemp)
keep_backup=0

cleanup() {
    status=$?
    trap - EXIT HUP INT TERM
    set +e
    rm -f "$temporary_config"
    if [ "$keep_backup" -eq 0 ]; then
        rm -f "$backup_config"
    else
        echo "WebSocket config backup preserved at $backup_config" >&2
    fi
    exit "$status"
}
trap cleanup EXIT HUP INT TERM

cp -p "$config" "$backup_config"
if ! awk -v include_line="$include_line" -v include_token="$include_token" \
    -v location_token="$location_token" '
    BEGIN { in_block = 0; has_include = 0 }
    {
        if (!in_block && index($0, "location") > 0 && index($0, location_token) > 0 && index($0, "{") > 0) {
            in_block = 1
            has_include = 0
            print
            next
        }
        if (in_block && index($0, include_token) > 0) {
            has_include = 1
        }
        if (in_block && $0 ~ /^[[:space:]]*}/) {
            if (!has_include) {
                print include_line
            }
            print
            in_block = 0
            next
        }
        print
    }
    END {
        if (in_block) {
            exit 1
        }
    }
' "$config" >"$temporary_config"; then
    echo "could not patch $config" >&2
    exit 1
fi

if ! validate_config "$temporary_config"; then
    echo "patched WebSocket config failed structural validation" >&2
    exit 1
fi

# Copy over the existing inode so the generated config keeps its ownership and
# mode inside the container.
if ! cp "$temporary_config" "$config"; then
    echo "could not install the patched WebSocket config" >&2
    exit 1
fi

if ! "$nginx_bin" -t >/dev/null 2>&1; then
    if ! cp -p "$backup_config" "$config"; then
        keep_backup=1
        echo "Nginx rejected the patch and the original config could not be restored" >&2
    else
        echo "Nginx rejected the WebSocket patch; original config restored" >&2
    fi
    exit 1
fi

if [ "$reload" -eq 1 ] && [ -s /run/nginx/nginx.pid ]; then
    "$nginx_bin" -s reload
fi

echo "WebSocket timeout patch applied"
