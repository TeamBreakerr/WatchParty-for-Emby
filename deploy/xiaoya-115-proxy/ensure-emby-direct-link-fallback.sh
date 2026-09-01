#!/bin/sh
set -eu

# Xiaoya's emby.js normally resolves an internal /d/ media path into a 302
# direct link.  WatchParty deliberately keeps those paths behind Emby's native
# backend so the dynamic 115 guard can enforce its two-connection limit.  The
# path is already known from PlaybackInfo, so route it without opening a
# one-byte media request first.  Local files already take the native backend
# branch, while unrelated ready-to-use external links retain Xiaoya's normal
# behavior.  Also remove the redundant stream.strm next-episode probe.

njs_script=${EMBY_NJS_SCRIPT:-/etc/nginx/http.d/emby.js}
nginx_bin=${NGINX_BIN:-nginx}
marker=codex-emby-guarded-path-routing-v2
legacy_marker=codex-emby-direct-link-fallback-v1
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
        && grep -Fq 'var isXiaoyaMediaPath =' "$file" \
        && grep -Fq 'r.return(302, embyRes);' "$file" \
        && grep -Fq 'r.internalRedirect("@backend")' "$file" \
        && grep -Fq 'PlaybackInfo?api_key=' "$file" \
        && ! fetch_has_media_probe "$file" \
        && ! grep -Fq 'var helperRedirectUrl = await fetchXYApi' "$file" \
        && ! grep -Fq 'getCachedXYUrl(alistFilePath' "$file" \
        && ! grep -Fq 'getCachedXYUrl(alistNextPath' "$file" \
        && ! grep -Fq '/stream.strm' "$file" \
        && ! grep -Fq "$legacy_marker" "$file"
}

fetch_has_media_probe() {
    awk '
        /^async function fetchXYApi\(xyurl, ua, cookie\)/ {
            in_fetch = 1
        }
        in_fetch && /"Range": "bytes=0-0"|error: media_body/ {
            found = 1
        }
        in_fetch && /^}/ {
            in_fetch = 0
        }
        END {
            exit found ? 0 : 1
        }
    ' "$1"
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
    echo "existing Emby guarded-path routing patch is incomplete" >&2
    exit 1
fi

has_legacy_patch=0
if grep -Fq "$legacy_marker" "$njs_script"; then
    has_legacy_patch=1
    if ! fetch_has_media_probe "$njs_script"; then
        echo "existing v1 Emby direct-link patch is incomplete" >&2
        exit 1
    fi
fi

remove_strm_preload=0
if grep -Fq '/stream.strm' "$njs_script"; then
    remove_strm_preload=1
fi

remove_next_preload=0
if grep -Fq 'getCachedXYUrl(alistNextPath' "$njs_script"; then
    remove_next_preload=1
fi

replace_helper=0
if grep -Fq 'var helperRedirectUrl = await fetchXYApi' "$njs_script"; then
    replace_helper=1
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

if ! awk -v marker="$marker" -v legacy_marker="$legacy_marker" \
    -v has_legacy_patch="$has_legacy_patch" \
    -v remove_strm_preload="$remove_strm_preload" \
    -v remove_next_preload="$remove_next_preload" \
    -v replace_helper="$replace_helper" '
    BEGIN {
        in_fetch = 0
        in_playback_path = 0
        drop_strm_preload = 0
        dropped_strm_preload = 0
        drop_media_body = 0
        drop_helper = 0
        drop_redirect_tail = 0
        helper_replaced = 0
        next_preload_removed = 0
        route_added = 0
        marker_added = 0
    }

    has_legacy_patch && index($0, legacy_marker) > 0 {
        next
    }

    /^async function fetchXYApi\(xyurl, ua, cookie\)/ {
        in_fetch = 1
        print
        next
    }

    has_legacy_patch && in_fetch && /"Range": "bytes=0-0"/ {
        next
    }

    has_legacy_patch && in_fetch &&
        /if \(res[.]status === 206 \|\| res[.]headers\["X-Emby-115-Proxy"\]/ {
        drop_media_body = 1
        next
    }

    drop_media_body {
        if ($0 ~ /^[[:space:]]*}[[:space:]]*$/) {
            drop_media_body = 0
        }
        next
    }

    in_fetch && /^}/ {
        in_fetch = 0
        print
        next
    }

    /^async function getPlaybackPath\(itemId, userId, apiKey, r\)/ {
        in_playback_path = 1
        print
        next
    }

    remove_strm_preload && in_playback_path && !dropped_strm_preload &&
        /^[[:space:]]*try[[:space:]]*\{/ {
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

    /await getCachedXYUrl\(alistNextPath, ua, nextItemId, cookie, r\);/ {
        print "                            // Guarded media is resolved when playback starts."
        next_preload_removed++
        next
    }

    /下一集预缓存成功:/ {
        sub(/下一集预缓存成功:/, "下一集媒体将在播放时解析:")
        print
        next
    }

    /^[[:space:]]*if \(contain115helper\)[[:space:]]*\{/ {
        print "    if (contain115helper) {"
        print "        r.internalRedirect(\"@backend\");"
        print "        return;"
        print "    }"
        drop_helper = 1
        helper_replaced++
        next
    }

    drop_helper {
        if ($0 ~ /^[[:space:]]*var alistFilePath = embyRes[.]replace/) {
            drop_helper = 0
        } else {
            next
        }
    }

    /^[[:space:]]*var alistFilePath = embyRes[.]replace/ {
        print "    // " marker
        print "    var isXiaoyaMediaPath ="
        print "        embyRes.includes(\"DOCKER_ADDRESS\") ||"
        print "        embyRes.includes(\"xiaoya.host:5678\") ||"
        print "        embyRes.includes(\"172.19.0.1:5678\") ||"
        print "        embyRes.indexOf(\"http://127.0.0.1:80/d/\") === 0;"
        print "    if (isXiaoyaMediaPath) {"
        print "        r.internalRedirect(\"@backend\");"
        print "        return;"
        print "    }"
        print ""
        print "    r.return(302, embyRes);"
        print "}"
        drop_redirect_tail = 1
        marker_added++
        route_added++
        next
    }

    drop_redirect_tail {
        if ($0 ~ /^}/) {
            drop_redirect_tail = 0
        }
        next
    }

    { print }

    END {
        if (marker_added != 1 || route_added != 1 ||
            (replace_helper && helper_replaced != 1) ||
            (remove_next_preload && next_preload_removed != 1) ||
            (remove_strm_preload && dropped_strm_preload != 1) ||
            drop_media_body || drop_helper || drop_strm_preload ||
            drop_redirect_tail) {
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
