#!/bin/sh
set -eu

# Keep Xiaoya's native direct-link behavior for ordinary playback.  Only the
# current item of an active WatchParty bypasses Xiaoya's early 302 resolution
# and stays behind Emby's backend, where the dynamic 115 Range guard can
# protect the two upstream leases.  The room decision is metadata-only; active
# room media skips Xiaoya's stream.strm and next-item probes.

njs_script=${EMBY_NJS_SCRIPT:-/etc/nginx/http.d/emby.js}
nginx_bin=${NGINX_BIN:-nginx}
marker=codex-emby-watchparty-routing-v4
previous_marker_v3=codex-emby-guarded-path-routing-v3
previous_marker_v2=codex-emby-guarded-path-routing-v2
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

fetch_has_media_probe() {
    awk '
        /^async function fetchXYApi\(xyurl, ua, cookie\)/ { in_fetch = 1 }
        in_fetch && /"Range": "bytes=0-0"|error: media_body/ { found = 1 }
        in_fetch && /^}/ { in_fetch = 0 }
        END { exit found ? 0 : 1 }
    ' "$1"
}

validate_patch() {
    file=$1
    grep -Fq "$marker" "$file" \
        && [ "$(grep -Fc "$marker" "$file")" -eq 1 ] \
        && grep -Fq 'async function isActiveWatchPartyItem' "$file" \
        && grep -Fq '/emby/WatchParty/List?api_key=' "$file" \
        && grep -Fq 'party.IsActive' "$file" \
        && grep -Fq 'party.CurrentEpisodeId' "$file" \
        && grep -Fq 'var isWatchPartyMedia = await isActiveWatchPartyItem(itemId, apiKey);' "$file" \
        && grep -Fq 'var isWatchPartyMedia = await isActiveWatchPartyItem(itemId, api_key);' "$file" \
        && grep -Fq 'var isLocalMediaPath =' "$file" \
        && grep -Fq 'var isXiaoyaMediaPath =' "$file" \
        && grep -Fq 'isWatchPartyMedia && isXiaoyaMediaPath' "$file" \
        && grep -Fq 'if (!isWatchPartyMedia) {' "$file" \
        && grep -Fq 'r.internalRedirect("@backend")' "$file" \
        && grep -Fq 'var helperRedirectUrl = await fetchXYApi' "$file" \
        && grep -Fq 'getCachedXYUrl(alistFilePath' "$file" \
        && grep -Fq 'getCachedXYUrl(alistNextPath' "$file" \
        && grep -Fq '/stream.strm' "$file" \
        && ! fetch_has_media_probe "$file" \
        && ! grep -Fq "$previous_marker_v3" "$file" \
        && ! grep -Fq "$previous_marker_v2" "$file" \
        && ! grep -Fq "$legacy_marker" "$file"
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
    echo "existing room-scoped Emby routing patch is incomplete" >&2
    exit 1
fi

has_previous_route=0
for old_marker in "$previous_marker_v3" "$previous_marker_v2" "$legacy_marker"; do
    if grep -Fq "$old_marker" "$njs_script"; then
        has_previous_route=1
    fi
done

has_legacy_probe=0
if grep -Fq "$legacy_marker" "$njs_script"; then
    has_legacy_probe=1
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

if ! awk -v marker="$marker" \
    -v previous_marker_v3="$previous_marker_v3" \
    -v previous_marker_v2="$previous_marker_v2" \
    -v legacy_marker="$legacy_marker" \
    -v has_previous_route="$has_previous_route" \
    -v has_legacy_probe="$has_legacy_probe" '
    BEGIN {
        in_fetch = 0
        in_playback_path = 0
        in_redirect = 0
        wrap_native_probe = 0
        drop_native_probe = 0
        drop_media_body = 0
        drop_old_redirect_tail = 0
        active_helper_added = 0
        playback_scope_added = 0
        redirect_scope_added = 0
        preload_scoped = 0
        route_added = 0
    }

    function print_active_helper() {
        print "var watchPartyItemCache = { key: null, expiresAt: 0, active: false };"
        print "async function isActiveWatchPartyItem(itemId, apiKey) {"
        print "    var cacheKey = String(apiKey || \"\") + \":\" + String(itemId || \"\");"
        print "    var now = Date.now();"
        print "    if (watchPartyItemCache.key === cacheKey && watchPartyItemCache.expiresAt > now) {"
        print "        return watchPartyItemCache.active;"
        print "    }"
        print "    var active = false;"
        print "    try {"
        print "        var partyUri = EMBY_HOST + \"/emby/WatchParty/List?api_key=\" + encodeURIComponent(apiKey);"
        print "        var response = await ngx.fetch(partyUri, {"
        print "            max_response_body_size: 65535,"
        print "            headers: { \"X-Emby-Token\": apiKey }"
        print "        });"
        print "        if (response.ok) {"
        print "            var data = await response.json();"
        print "            var parties = data && data.Parties ? data.Parties : [];"
        print "            active = parties.some(function(party) {"
        print "                return party && party.IsActive && ("
        print "                    String(party.ItemId || \"\") === String(itemId) ||"
        print "                    String(party.CurrentEpisodeId || \"\") === String(itemId)"
        print "                );"
        print "            });"
        print "        }"
        print "    } catch (e) {}"
        print "    watchPartyItemCache = { key: cacheKey, expiresAt: now + 1000, active: active };"
        print "    return active;"
        print "}"
        print ""
        active_helper_added++
    }

    function print_native_stream_probe() {
        print "    if (!isWatchPartyMedia) {"
        print "        try {"
        print "            var strmUri = EMBY_HOST + '\''/emby/Videos/'\'' + itemId + '\''/stream.strm?api_key='\'' + apiKey;"
        print "            var res = await ngx.fetch(strmUri, {"
        print "                max_response_body_size: 65535,"
        print "                headers: { '\''X-Emby-Token'\'': apiKey }"
        print "            });"
        print "            if (res.ok) {"
        print "                var content = await res.text();"
        print "                var url = content.trim();"
        print "                if (url && (url.startsWith('\''http://'\'') || url.startsWith('\''https://'\''))) {"
        print "                    return url;"
        print "                }"
        print "                if (url && url.includes('\''DOCKER_ADDRESS'\'')) {"
        print "                    return url;"
        print "                }"
        print "            }"
        print "        } catch (e) {}"
        print "    }"
        print ""
    }

    function print_route_guard() {
        print "    // " marker
        print "    var isLocalMediaPath ="
        print "        doesNotContainHttp && doesNotContainDOCKER;"
        print "    var isXiaoyaMediaPath ="
        print "        embyRes.includes(\"DOCKER_ADDRESS\") ||"
        print "        embyRes.includes(\"xiaoya.host:5678\") ||"
        print "        embyRes.includes(\"172.19.0.1:5678\") ||"
        print "        embyRes.indexOf(\"http://127.0.0.1:80/d/\") === 0;"
        print "    var isGuardedWatchPartyPath = isWatchPartyMedia && isXiaoyaMediaPath;"
        print "    if (isLocalMediaPath || isGuardedWatchPartyPath ||"
        print "        (isWatchPartyMedia && contain115helper)) {"
        print "        r.internalRedirect(\"@backend\");"
        print "        return;"
        print "    }"
        print ""
        route_added++
    }

    function print_native_redirect_tail() {
        print "    var contain115helper = embyRes.includes(\"P115StrmHelper\");"
        print_route_guard()
        print "    if (contain115helper) {"
        print "        var helperRedirectUrl = await fetchXYApi(embyRes, ua, cookie);"
        print "        if (helperRedirectUrl.startsWith('\''error'\'')) {"
        print "            r.internalRedirect(\"@backend\");"
        print "            return;"
        print "        }"
        print "        r.return(302, helperRedirectUrl);"
        print "        return;"
        print "    }"
        print ""
        print "    var alistFilePath = embyRes.replace('\''DOCKER_ADDRESS'\'', '\''http://127.0.0.1:80'\'') + '\''?sign='\'';"
        print "    var alistRes = await getCachedXYUrl(alistFilePath, ua, itemId, cookie, r);"
        print ""
        print "    if (!alistRes.startsWith('\''error'\'')) {"
        print "        if (alistRes.indexOf(\"http\") !== -1) {"
        print "            r.return(302, alistRes);"
        print "            return;"
        print "        }"
        print "    }"
        print ""
        print "    r.return(500, alistRes);"
        print "}"
    }

    index($0, previous_marker_v3) || index($0, previous_marker_v2) ||
        index($0, legacy_marker) {
        if (in_fetch && index($0, legacy_marker)) next
    }

    /^async function fetchXYApi\(xyurl, ua, cookie\)/ {
        in_fetch = 1
        print
        next
    }

    has_legacy_probe && in_fetch && /"Range": "bytes=0-0"/ { next }

    has_legacy_probe && in_fetch &&
        /if \(res[.]status === 206 \|\| res[.]headers\["X-Emby-115-Proxy"\]/ {
        drop_media_body = 1
        next
    }

    drop_media_body {
        if ($0 ~ /^[[:space:]]*}[[:space:]]*$/) drop_media_body = 0
        next
    }

    in_fetch && /^}/ {
        in_fetch = 0
        print
        next
    }

    /^async function getPlaybackPath\(itemId, userId, apiKey, r\)/ {
        print_active_helper()
        in_playback_path = 1
        print
        print "    var isWatchPartyMedia = await isActiveWatchPartyItem(itemId, apiKey);"
        playback_scope_added++
        if (has_previous_route) {
            print_native_stream_probe()
            if (has_legacy_probe) drop_native_probe = 1
        } else {
            wrap_native_probe = 1
        }
        next
    }

    drop_native_probe {
        if (index($0, "} catch (e) {}") > 0) drop_native_probe = 0
        next
    }

    in_playback_path && wrap_native_probe && /^[[:space:]]*try[[:space:]]*\{/ {
        print "    if (!isWatchPartyMedia) {"
        print
        wrap_native_probe = 2
        next
    }

    in_playback_path && wrap_native_probe == 2 && /} catch \(e\) \{}/ {
        print
        print "    }"
        wrap_native_probe = 0
        next
    }

    in_playback_path && /^}/ {
        in_playback_path = 0
        print
        next
    }

    /^async function redirect2Pan\(r\)/ {
        in_redirect = 1
        print
        next
    }

    in_redirect && /^[[:space:]]*var api_key[[:space:]]*=/ {
        print
        print "    var isWatchPartyMedia = await isActiveWatchPartyItem(itemId, api_key);"
        redirect_scope_added++
        in_redirect = 0
        next
    }

    /await getCachedXYUrl\(alistNextPath, ua, nextItemId, cookie, r\);/ ||
        /Guarded media is resolved when playback starts[.]/ {
        print "        if (!isWatchPartyMedia) {"
        print "            await getCachedXYUrl(alistNextPath, ua, nextItemId, cookie, r);"
        print "        }"
        preload_scoped++
        next
    }

    has_previous_route && /^[[:space:]]*var contain115helper =/ {
        print_native_redirect_tail()
        drop_old_redirect_tail = 1
        next
    }

    drop_old_redirect_tail {
        if ($0 ~ /^}/) drop_old_redirect_tail = 0
        next
    }

    !has_previous_route && /^[[:space:]]*var contain115helper =/ {
        print
        print_route_guard()
        next
    }

    { print }

    END {
        if (active_helper_added != 1 || playback_scope_added != 1 ||
            redirect_scope_added != 1 || preload_scoped != 1 ||
            route_added != 1 || wrap_native_probe || drop_native_probe ||
            drop_media_body || drop_old_redirect_tail) {
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
    echo "could not install the room-scoped Emby routing patch" >&2
    exit 1
fi

if ! "$nginx_bin" -t >/dev/null 2>&1; then
    echo "Nginx rejected the Emby direct-link fallback patch" >&2
    exit 1
fi

restore_needed=0
reload_nginx
echo "patched"
