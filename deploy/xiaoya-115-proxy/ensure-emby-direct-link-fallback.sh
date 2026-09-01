#!/bin/sh
set -eu

# Xiaoya's emby.js expects /d/ to return a 302 direct link.  The WatchParty
# 115 guard deliberately turns that redirect into a proxied 200/206 media
# response.  Probe with one byte, recognize that response without buffering
# the video in njs, and let Emby's native backend consume the guarded path.
# Also remove Xiaoya's stream.strm next-episode probe: PlaybackInfo immediately
# below it already supplies the real remote path without starting FFmpeg.

njs_script=${EMBY_NJS_SCRIPT:-/etc/nginx/http.d/emby.js}
nginx_bin=${NGINX_BIN:-nginx}
marker=codex-emby-direct-link-fallback-v1
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

validate_patch() {
    file=$1
    grep -Fq "$marker" "$file" \
        && [ "$(grep -c "$marker" "$file")" -eq 1 ] \
        && grep -Fq '"Range": "bytes=0-0"' "$file" \
        && grep -Fq 'error: media_body' "$file" \
        && grep -Fq 'r.internalRedirect("@backend")' "$file" \
        && grep -Fq 'PlaybackInfo?api_key=' "$file" \
        && ! grep -Fq '/stream.strm' "$file"
}

reload_nginx() {
    if [ "$reload" -eq 1 ] && [ -s /run/nginx/nginx.pid ]; then
        "$nginx_bin" -s reload
    fi
}

if validate_patch "$njs_script"; then
    "$nginx_bin" -t >/dev/null 2>&1
    reload_nginx
    echo "already-present"
    exit 0
fi

if grep -Fq "$marker" "$njs_script"; then
    echo "existing Emby direct-link fallback patch is incomplete" >&2
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
        if ! cp -p "$backup_script" "$njs_script"; then
            keep_backup=1
            echo "could not restore the previous Xiaoya Emby njs script" >&2
        fi
    fi
    rm -f "$temporary_script"
    if [ "$keep_backup" -eq 0 ]; then
        rm -f "$backup_script"
    else
        echo "Emby njs backup preserved at $backup_script" >&2
    fi
    exit "$exit_status"
}
trap cleanup EXIT
trap 'exit 1' HUP INT TERM

cp -p "$njs_script" "$backup_script"
restore_needed=1

if ! awk -v marker="$marker" '
    BEGIN {
        in_fetch = 0
        in_playback_path = 0
        drop_strm_preload = 0
        dropped_strm_preload = 0
        range_added = 0
        media_body_added = 0
        backend_fallback_added = 0
        marker_added = 0
    }

    /^async function fetchXYApi\(xyurl, ua, cookie\)/ {
        in_fetch = 1
        print
        print "    // " marker
        marker_added++
        next
    }

    in_fetch && /"X-Alist-OriUA": ua/ {
        sub(/[[:space:]]*$/, "")
        if ($0 !~ /,[[:space:]]*$/) {
            $0 = $0 ","
        }
        print
        print "                \"Range\": \"bytes=0-0\""
        range_added++
        next
    }

    in_fetch && /var text = await res[.]text\(\);/ {
        print "        if (res.status === 206 || res.headers[\"X-Emby-115-Proxy\"] || res.headers[\"x-emby-115-proxy\"]) {"
        print "            return \"error: media_body\";"
        print "        }"
        media_body_added++
        print
        next
    }

    /^async function getPlaybackPath\(itemId, userId, apiKey, r\)/ {
        in_fetch = 0
        in_playback_path = 1
        print
        next
    }

    in_playback_path && !dropped_strm_preload && /^[[:space:]]*try[[:space:]]*\{/ {
        drop_strm_preload = 1
        next
    }

    drop_strm_preload {
        if (index($0, "} catch (e) {}") > 0) {
            drop_strm_preload = 0
            dropped_strm_preload++
        }
        next
    }

    /^[[:space:]]*r[.]return\(500, alistRes\);[[:space:]]*$/ {
        print "    r.internalRedirect(\"@backend\");"
        backend_fallback_added++
        next
    }

    { print }

    END {
        if (marker_added != 1 || range_added != 1 || media_body_added != 1 ||
            dropped_strm_preload != 1 || backend_fallback_added != 1) {
            exit 3
        }
    }
' "$njs_script" >"$temporary_script"; then
    echo "unrecognized Xiaoya emby.js layout; no changes installed" >&2
    exit 1
fi

if ! validate_patch "$temporary_script"; then
    echo "patched Xiaoya emby.js failed structural validation" >&2
    exit 1
fi

if ! cp "$temporary_script" "$njs_script"; then
    echo "could not install the Emby direct-link fallback patch" >&2
    exit 1
fi

if ! "$nginx_bin" -t >/dev/null 2>&1; then
    echo "Nginx rejected the Emby direct-link fallback patch" >&2
    exit 1
fi

restore_needed=0
reload_nginx
echo "patched"
