#!/bin/sh
set -eu

# Xiaoya's generated `emby.conf` gives its Emby server blocks a 20 second
# `proxy_read_timeout`, and every location that proxies to Emby inherits it.
# That is too short for this library: a recursive collection query over a
# 4.4 GB library.db took longer, and Nginx dropped it with
#   upstream timed out (110) while reading response header from upstream
# for an "Emby for iOS" request, which the client sees as a failed load.
#
# Only the three timeouts that bound Emby's own response are raised.
# `proxy_connect_timeout` still fails fast when Emby is unreachable, and the
# client-side header/body timeouts are left alone. The WebSocket locations keep
# their separate 24-hour include, which is a different concern: an idle tunnel
# rather than a slow answer.

emby_config=${EMBY_NGINX_CONFIG:-/etc/nginx/http.d/emby.conf}
nginx_bin=${NGINX_BIN:-nginx}
timeout_value=${EMBY_PROXY_TIMEOUT:-300s}
marker='# watchparty-emby-proxy-timeout'
reload=0

case "${1:-}" in
    "") ;;
    --reload) reload=1 ;;
    *)
        echo "usage: $0 [--reload]" >&2
        exit 2
        ;;
esac

if [ ! -r "$emby_config" ]; then
    echo "missing Xiaoya Emby config: $emby_config" >&2
    exit 1
fi

# The container's grep prints nothing for -c when there are no matches, so a
# numeric test on its output errors instead of comparing.
count_lines() {
    awk -v needle="$2" 'index($0, needle) > 0 { found++ } END { print found + 0 }' "$1"
}

# Every server-scope directive that bounds how long Emby may take to answer.
raised_directives='proxy_read_timeout proxy_send_timeout send_timeout'

validate_timeouts() {
    file=$1
    awk -v value="$timeout_value" -v marker="$marker" -v names="$raised_directives" '
        BEGIN {
            split(names, wanted, " ")
            for (i in wanted) required[wanted[i]] = 1
        }
        # Only server-scope directives are considered; a location may legitimately
        # set its own, as the WebSocket include does.
        /^[[:space:]]{4}[a-z_]+[[:space:]]/ {
            name = $1
            if (!(name in required)) next
            seen[name]++
            if ($0 !~ value ";" || index($0, marker) == 0) bad++
        }
        END {
            for (name in required) {
                if (!(name in seen)) exit 1
            }
            exit bad ? 1 : 0
        }
    ' "$file"
}

reload_nginx() {
    if [ "$reload" -eq 1 ] && [ -s /run/nginx/nginx.pid ]; then
        "$nginx_bin" -s reload
    fi
}

if validate_timeouts "$emby_config"; then
    "$nginx_bin" -t >/dev/null 2>&1
    reload_nginx
    echo "already-present"
    exit 0
fi

temporary_config=$(mktemp)
backup_config=$(mktemp)
restore_needed=0
keep_backup=0

cleanup() {
    exit_status=$?
    trap - EXIT HUP INT TERM
    set +e
    if [ "$exit_status" -ne 0 ] && [ "$restore_needed" -eq 1 ]; then
        cp -p "$backup_config" "$emby_config" || keep_backup=1
        if [ "$keep_backup" -eq 1 ]; then
            echo "could not restore $emby_config" >&2
        fi
    fi
    rm -f "$temporary_config"
    if [ "$keep_backup" -eq 0 ]; then
        rm -f "$backup_config"
    else
        echo "config backup preserved at $backup_config" >&2
    fi
    exit "$exit_status"
}
trap cleanup EXIT
trap 'exit 1' HUP INT TERM

cp -p "$emby_config" "$backup_config"
restore_needed=1

if ! awk -v value="$timeout_value" -v marker="$marker" -v names="$raised_directives" '
    BEGIN {
        split(names, wanted, " ")
        for (i in wanted) required[wanted[i]] = 1
    }
    # Rewrite only at server scope: a location that sets its own timeout, such
    # as the 24-hour WebSocket include, must keep it.
    /^[[:space:]]{4}[a-z_]+[[:space:]]/ && ($1 in required) {
        print "    " $1 " " value "; " marker
        rewritten++
        next
    }
    { print }
    END { if (rewritten < 1) exit 3 }
' "$emby_config" >"$temporary_config"; then
    echo "no Emby server timeouts found in $emby_config" >&2
    exit 1
fi

if ! validate_timeouts "$temporary_config"; then
    echo "rewritten $emby_config failed timeout validation" >&2
    exit 1
fi

if ! cp "$temporary_config" "$emby_config"; then
    echo "could not install the Emby proxy timeouts" >&2
    exit 1
fi

if ! "$nginx_bin" -t >/dev/null 2>&1; then
    echo "Nginx rejected the Emby proxy timeouts" >&2
    exit 1
fi

restore_needed=0
reload_nginx
echo "patched"
