#!/bin/sh
set -eu

# A placeholder resolution is a failure, not media.
#
# When Xiaoya cannot produce a real download link - an expired share, a failed
# `AliyundriveShare2Pan115` transfer, provider rate limiting - its `/d/` handler
# answers `302` to `img.xiaoya.pro/abnormal.png`. That is a successful HTTP
# redirect carrying an image, so nothing downstream recognises it as an error:
# `getCachedXYUrl` stores it for the full cache lifetime and `redirect2Pan`
# hands it to the client as though it were the video. The client then downloads
# a PNG and fails, and it keeps failing for hours after the provider recovers
# because the placeholder is pinned in the cache.
#
# So never cache a placeholder, and answer the request with a gateway error the
# client can report honestly instead of a redirect it cannot play.

njs_script=${EMBY_NJS_SCRIPT:-/etc/nginx/http.d/emby.js}
nginx_bin=${NGINX_BIN:-nginx}
placeholder=${EMBY_PLACEHOLDER_MARKER:-xiaoya.pro/abnormal}
marker=codex-emby-placeholder-guard-v1
cache_anchor='if (!isError && !isHtmlError && !isEmpty && !isJsonError) {'
resolve_anchor='var alistRes = await getCachedXYUrl(alistFilePath, ua, itemId, cookie, r);'
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

# `grep -c` cannot be used for these counts: the container ships a grep whose
# -c prints nothing at all when there are no matches, so a numeric test on its
# output errors instead of comparing.
count_lines() {
    awk -v needle="$2" 'index($0, needle) > 0 { found++ } END { print found + 0 }' "$1"
}

validate_guard() {
    file=$1
    [ "$(count_lines "$file" "$marker")" -eq 2 ] \
        && grep -Fq 'var isPlaceholderLink = result.indexOf(' "$file" \
        && grep -Fq '&& !isPlaceholderLink) {' "$file" \
        && grep -Fq 'if (alistRes.indexOf("'"$placeholder"'") !== -1) {' "$file" \
        && grep -Fq 'r.return(502, ' "$file" \
        && [ "$(count_lines "$file" "$cache_anchor")" -eq 0 ] \
        && [ "$(count_lines "$file" "$resolve_anchor")" -eq 1 ]
}

reload_nginx() {
    if [ "$reload" -eq 1 ] && [ -s /run/nginx/nginx.pid ]; then
        "$nginx_bin" -s reload
    fi
}

if validate_guard "$njs_script"; then
    "$nginx_bin" -t >/dev/null 2>&1
    reload_nginx
    echo "already-present"
    exit 0
fi

if grep -Fq "$marker" "$njs_script"; then
    echo "existing placeholder guard is incomplete" >&2
    exit 1
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
    -v placeholder="$placeholder" \
    -v cache_anchor="$cache_anchor" \
    -v resolve_anchor="$resolve_anchor" '
    function indent_of(line,   pad) {
        pad = line
        sub(/[^[:space:]].*$/, "", pad)
        return pad
    }

    index($0, cache_anchor) > 0 && !cache_guarded {
        pad = indent_of($0)
        print pad "// " marker
        print pad "var isPlaceholderLink = result.indexOf(\"" placeholder "\") !== -1;"
        sub(/\) \{[[:space:]]*$/, " \\&\\& !isPlaceholderLink) {")
        print
        cache_guarded++
        next
    }

    index($0, resolve_anchor) > 0 && !resolve_guarded {
        print
        pad = indent_of($0)
        print ""
        print pad "// " marker
        print pad "if (alistRes.indexOf(\"" placeholder "\") !== -1) {"
        print pad "    r.return(502, \"error: provider returned a placeholder\");"
        print pad "    return;"
        print pad "}"
        resolve_guarded++
        next
    }

    { print }

    END { if (cache_guarded != 1 || resolve_guarded != 1) exit 3 }
' "$njs_script" >"$temporary_script"; then
    echo "could not locate the link resolution anchors in $njs_script" >&2
    exit 1
fi

if ! validate_guard "$temporary_script"; then
    echo "patched Xiaoya emby.js failed placeholder guard validation" >&2
    exit 1
fi

if ! cp "$temporary_script" "$njs_script"; then
    echo "could not install the placeholder guard" >&2
    exit 1
fi

if ! "$nginx_bin" -t >/dev/null 2>&1; then
    echo "Nginx rejected the placeholder guard" >&2
    exit 1
fi

restore_needed=0
reload_nginx
echo "patched"
