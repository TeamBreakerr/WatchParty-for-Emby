#!/bin/sh
set -eu

# Xiaoya's emby.js turns an internal media path into a signed direct link by
# fetching Xiaoya's own port 80 `/d/` location.  The dynamic 115 guard now owns
# that location and answers with the media body in 1 MiB slices instead of the
# OpenList 302.  njs caps `ngx.fetch` at `max_response_body_size`, so a
# multi-gigabyte body raises an exception, `fetchXYApi` returns
# `error: xy_api fetch failed`, and `redirect2Pan` answers HTTP 500 to every
# direct-play client.  Resolve against OpenList's own listener instead: it
# returns the signed redirect and never streams a body, so the guard keeps
# protecting real reads while link resolution stays metadata-only.

njs_script=${EMBY_NJS_SCRIPT:-/etc/nginx/http.d/emby.js}
nginx_bin=${NGINX_BIN:-nginx}
guarded_origin=${EMBY_GUARDED_ORIGIN:-http://127.0.0.1:80}
openlist_origin=${EMBY_OPENLIST_ORIGIN:-http://127.0.0.1:5244}
marker=codex-emby-openlist-resolver-v1
reload=0

case "${1:-}" in
    "") ;;
    --reload) reload=1 ;;
    *)
        echo "usage: $0 [--reload]" >&2
        exit 2
        ;;
esac

if [ ! -r "$njs_script" ]; then
    echo "missing Xiaoya Emby njs script: $njs_script" >&2
    exit 1
fi

validate_resolver() {
    awk -v marker="$marker" \
        -v guarded="'$guarded_origin'" \
        -v openlist="'$openlist_origin'" '
        index($0, marker) > 0 { markers++ }
        /var alist(File|Next)Path[[:space:]]*=/ {
            assignments++
            if (index($0, guarded) > 0) guarded_left++
            if (index($0, openlist) == 0) openlist_missing++
        }
        END {
            exit (assignments > 0 && markers == assignments &&
                guarded_left == 0 && openlist_missing == 0) ? 0 : 1
        }
    ' "$1"
}

reload_nginx() {
    if [ "$reload" -eq 1 ] && [ -s /run/nginx/nginx.pid ]; then
        "$nginx_bin" -s reload
    fi
}

if validate_resolver "$njs_script"; then
    "$nginx_bin" -t >/dev/null 2>&1
    reload_nginx
    echo "already-present"
    exit 0
fi

temporary_script=$(mktemp)
backup_script=$(mktemp)
restore_needed=0
keep_backup=0

cleanup() {
    exit_status=$?
    trap - EXIT HUP INT TERM
    set +e
    if [ "$exit_status" -ne 0 ] && [ "$restore_needed" -eq 1 ]; then
        cp -p "$backup_script" "$njs_script" || keep_backup=1
        if [ "$keep_backup" -eq 1 ]; then
            echo "could not restore $njs_script" >&2
        fi
    fi
    rm -f "$temporary_script"
    if [ "$keep_backup" -eq 0 ]; then
        rm -f "$backup_script"
    else
        echo "njs backup preserved at $backup_script" >&2
    fi
    exit "$exit_status"
}
trap cleanup EXIT
trap 'exit 1' HUP INT TERM

cp -p "$njs_script" "$backup_script"
restore_needed=1

if ! awk -v marker="$marker" \
    -v guarded="'$guarded_origin'" \
    -v openlist="'$openlist_origin'" '
    function replace_all(line, from, to,   out, at) {
        out = ""
        while ((at = index(line, from)) > 0) {
            out = out substr(line, 1, at - 1) to
            line = substr(line, at + length(from))
        }
        return out line
    }

    # Drop a marker left by an earlier, incomplete run so the rewrite below
    # always produces exactly one marker per resolved assignment.
    /^[[:space:]]*\/\// && index($0, marker) > 0 { next }

    /var alist(File|Next)Path[[:space:]]*=/ {
        indent = $0
        sub(/[^[:space:]].*$/, "", indent)
        print indent "// " marker
        print replace_all($0, guarded, openlist)
        rewritten++
        next
    }

    { print }

    END { if (rewritten < 1) exit 3 }
' "$njs_script" >"$temporary_script"; then
    echo "no OpenList direct-link resolution found in $njs_script" >&2
    exit 1
fi

if ! validate_resolver "$temporary_script"; then
    echo "rewritten Xiaoya emby.js failed OpenList resolver validation" >&2
    exit 1
fi

if ! cp "$temporary_script" "$njs_script"; then
    echo "could not install the OpenList resolver rewrite" >&2
    exit 1
fi

if ! "$nginx_bin" -t >/dev/null 2>&1; then
    echo "Nginx rejected the OpenList resolver rewrite" >&2
    exit 1
fi

restore_needed=0
reload_nginx
echo "patched"
