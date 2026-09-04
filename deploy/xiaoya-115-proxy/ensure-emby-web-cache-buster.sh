#!/bin/sh
set -eu

config=${EMBY_NGINX_CONFIG:-/etc/nginx/http.d/emby.conf}
include_file=${EMBY_WEB_CACHE_INCLUDE:-/data/emby-web-cache-buster.conf}
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
    echo "missing Web cache-buster include: $include_file" >&2
    exit 1
fi

# The rewrite has to name the version Emby actually serves. Pinning it means an
# Emby upgrade silently stops the rewrite from matching, browsers keep the
# cached unpatched playbackmanager.js, and nothing reports a problem. Read the
# live version and rewrite the include when it has moved.
emby_origin=${EMBY_ORIGIN:-http://emby:6908}
live_version=$(curl -sS -m 10 "$emby_origin/web/index.html" 2>/dev/null \
    | sed -n 's/.*data-appversion="\([^"]*\)".*/\1/p' | head -n 1)
case $live_version in
    ''|*-wp*) ;;
    *)
        pinned=$(sed -n "s/.*sub_filter 'data-appversion=\"\([^\"]*\)\"'.*/\1/p" \
            "$include_file" | head -n 1)
        if [ -n "$pinned" ] && [ "$pinned" != "$live_version" ]; then
            suffix=$(sed -n "s/.*'data-appversion=\"[^\"]*-\(wp[0-9]*\)\"';.*/\1/p" \
                "$include_file" | head -n 1)
            suffix=${suffix:-wp1}
            echo "Emby version moved from $pinned to $live_version; retargeting" \
                "the Web cache buster" >&2
            retarget=$(mktemp)
            if awk -v v="$live_version" -v s="$suffix" '
                /sub_filter .data-appversion=/ {
                    print "sub_filter '\''data-appversion=\"" v "\"'\'' " \
                        "'\''data-appversion=\"" v "-" s "\"'\'';"
                    next
                }
                { print }
            ' "$include_file" >"$retarget" && [ -s "$retarget" ]; then
                cat "$retarget" >"$include_file"
            fi
            rm -f "$retarget"
        fi
        ;;
esac

location_token='/web/index.html'
include_token=$include_file
include_line="        include $include_file;"

validate_config() {
    awk -v include_token="$include_token" -v location_token="$location_token" '
        BEGIN {
            in_block = 0
            depth = 0
            block_count = 0
            missing_count = 0
        }
        {
            if (!in_block && $0 ~ /^[[:space:]]*location[[:space:]]/ &&
                index($0, location_token) > 0 && index($0, "{") > 0) {
                in_block = 1
                depth = 0
                has_include = 0
                block_count++
            }

            if (in_block) {
                if (index($0, include_token) > 0) {
                    has_include = 1
                }
                line = $0
                opens = gsub(/\{/, "{", line)
                closes = gsub(/\}/, "}", line)
                depth += opens - closes
                if (depth <= 0) {
                    if (!has_include) {
                        missing_count++
                    }
                    in_block = 0
                }
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
        if [ -s /run/nginx/nginx.pid ]; then
            "$nginx_bin" -s reload
        fi
    fi
    echo "already-present"
    exit 0
fi

temporary_config=$(mktemp)
backup_config=$(mktemp)
restore_needed=0
keep_backup=0

cleanup() {
    status=$?
    trap - EXIT HUP INT TERM
    set +e
    if [ "$status" -ne 0 ] && [ "$restore_needed" -eq 1 ]; then
        if ! cp -p "$backup_config" "$config"; then
            keep_backup=1
            echo "could not restore the previous Web cache-buster configuration" >&2
        fi
    fi
    rm -f "$temporary_config"
    if [ "$keep_backup" -eq 0 ]; then
        rm -f "$backup_config"
    else
        echo "Web cache-buster backup preserved at $backup_config" >&2
    fi
    exit "$status"
}
trap cleanup EXIT
trap 'exit 1' HUP INT TERM

cp -p "$config" "$backup_config"
restore_needed=1

if ! awk -v include_line="$include_line" -v include_token="$include_token" \
    -v location_token="$location_token" '
    BEGIN {
        in_block = 0
        depth = 0
        block_count = 0
    }
    {
        is_target = (!in_block && $0 ~ /^[[:space:]]*location[[:space:]]/ &&
            index($0, location_token) > 0 && index($0, "{") > 0)
        if (is_target) {
            in_block = 1
            depth = 0
            has_include = 0
            block_count++
        }

        if (in_block && index($0, include_token) > 0) {
            has_include = 1
        }

        line = $0
        opens = gsub(/\{/, "{", line)
        closes = gsub(/\}/, "}", line)
        next_depth = depth + opens - closes
        if (in_block && next_depth <= 0) {
            if (!has_include) {
                print include_line
            }
            print
            in_block = 0
            depth = 0
            next
        }

        print
        if (in_block) {
            depth = next_depth
        }
    }
    END {
        if (in_block || block_count == 0) {
            exit 1
        }
    }
' "$config" >"$temporary_config"; then
    echo "could not patch $config" >&2
    exit 1
fi

if ! validate_config "$temporary_config"; then
    echo "patched Web cache-buster configuration failed structural validation" >&2
    exit 1
fi
if ! cp "$temporary_config" "$config"; then
    echo "could not install the Web cache-buster configuration" >&2
    exit 1
fi

if ! "$nginx_bin" -t >/dev/null 2>&1; then
    echo "Nginx rejected the Web cache-buster patch; original configuration will be restored" >&2
    exit 1
fi

restore_needed=0
if [ "$reload" -eq 1 ] && [ -s /run/nginx/nginx.pid ]; then
    "$nginx_bin" -s reload
fi

echo "patched"
