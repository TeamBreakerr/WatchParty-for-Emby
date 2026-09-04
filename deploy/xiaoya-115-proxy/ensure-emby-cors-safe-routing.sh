#!/bin/sh
set -eu

# A direct link is useless to a client that will apply CORS checks to it.
#
# Emby Web sets `crossOrigin` on its video element whenever a subtitle stream
# is selected at playback start, so it can read frames back for the subtitle
# overlay.  That makes the media load a CORS request, and CORS survives
# redirects: when Xiaoya answers `/videos/*/original` with a 302 to the
# provider's CDN, the browser checks the CDN's response for an
# `Access-Control-Allow-Origin` header.  115's CDN sends none, so the load is
# blocked before a single byte is decoded:
#
#   Access to video at 'https://cdnfhnfile.115cdn.net/...' (redirected from
#   '.../videos/2131360/original.mkv') has been blocked by CORS policy:
#   No 'Access-Control-Allow-Origin' header is present on the requested
#   resource.  →  net::ERR_FAILED  →  playback error type: medianotsupported
#
# Emby Web then falls back to a transcode, so playback recovers - but every
# such play wastes the round trip and files a spurious `DirectPlayError` that
# makes the server logs much harder to read.
#
# `Sec-Fetch-Mode: cors` is exactly that condition, stated by the client
# itself.  Measured against a same-origin echo server: a video element sends
# `no-cors` without `crossOrigin` and `cors` with it, and `Origin` is absent in
# both cases - which is why this keys off the fetch mode and not off `Origin`.
# Fetch metadata headers are browser-only, so native players (Emby for iOS,
# Filmly, ffmpeg-based clients) never send them and keep their direct link, and
# a browser playing without subtitles keeps its direct link too.

njs_script=${EMBY_NJS_SCRIPT:-/etc/nginx/http.d/emby.js}
nginx_bin=${NGINX_BIN:-nginx}
marker=codex-emby-cors-safe-routing-v1
anchor='if (r.uri.indexOf("Subtitles") !== -1) {'
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

# The container's grep prints nothing for -c when there are no matches, which
# would make a numeric test error out instead of comparing.
count_lines() {
    awk -v needle="$2" 'index($0, needle) > 0 { found++ } END { print found + 0 }' "$1"
}

validate_cors_route() {
    file=$1
    [ "$(count_lines "$file" "$marker")" -eq 1 ] \
        && grep -Fq 'var corsCheckedMedia = r.headersIn["Sec-Fetch-Mode"];' "$file" \
        && grep -Fq 'if (corsCheckedMedia && corsCheckedMedia.toLowerCase() === "cors") {' "$file" \
        && [ "$(count_lines "$file" "$anchor")" -eq 1 ]
}

reload_nginx() {
    if [ "$reload" -eq 1 ] && [ -s /run/nginx/nginx.pid ]; then
        "$nginx_bin" -s reload
    fi
}

if validate_cors_route "$njs_script"; then
    "$nginx_bin" -t >/dev/null 2>&1
    reload_nginx
    echo "already-present"
    exit 0
fi

if grep -Fq "$marker" "$njs_script"; then
    echo "existing CORS routing patch is incomplete" >&2
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

if ! awk -v marker="$marker" -v anchor="$anchor" '
    index($0, anchor) > 0 && !added {
        indent = $0
        sub(/[^[:space:]].*$/, "", indent)
        print indent "// " marker
        print indent "var corsCheckedMedia = r.headersIn[\"Sec-Fetch-Mode\"];"
        print indent "if (corsCheckedMedia && corsCheckedMedia.toLowerCase() === \"cors\") {"
        print indent "    r.internalRedirect(\"@backend\");"
        print indent "    return;"
        print indent "}"
        print ""
        added++
    }

    { print }

    END { if (added != 1) exit 3 }
' "$njs_script" >"$temporary_script"; then
    echo "could not locate the njs request-class guard in $njs_script" >&2
    exit 1
fi

if ! validate_cors_route "$temporary_script"; then
    echo "patched Xiaoya emby.js failed CORS routing validation" >&2
    exit 1
fi

if ! cp "$temporary_script" "$njs_script"; then
    echo "could not install the CORS routing patch" >&2
    exit 1
fi

if ! "$nginx_bin" -t >/dev/null 2>&1; then
    echo "Nginx rejected the CORS routing patch" >&2
    exit 1
fi

restore_needed=0
reload_nginx
echo "patched"
