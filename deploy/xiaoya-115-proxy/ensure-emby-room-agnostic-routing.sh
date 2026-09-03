#!/bin/sh
set -eu

# Xiaoya's emby.js must route by request class alone, never by Watch Party
# membership.
#
# An earlier revision asked the authenticated WatchParty/List endpoint on every
# playback request and kept an active room's media behind Emby's backend "so the
# 115 guard can protect the read" - which is circular: it created the server-side
# read it then protected.  It also rested on a wrong premise.  The provider does
# not cap concurrent connections; it serves two reads that *start together* and
# rejects a third.  Two participants therefore direct-play the same file
# perfectly, and a larger party is kept inside that burst by spacing the
# server-issued commands (ProviderBurstPacer), which is the only place a burst is
# actually created.
#
# What remains is decided by what the client asked for, not by who is watching:
# a local path and a server-rendered manifest stay on Emby's backend, everything
# else keeps Xiaoya's native direct link.  This script removes the room-aware
# patch and validates that nothing reintroduces it.

njs_script=${EMBY_NJS_SCRIPT:-/etc/nginx/http.d/emby.js}
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

if [ ! -r "$njs_script" ]; then
    echo "missing Xiaoya Emby njs script: $njs_script" >&2
    exit 1
fi

validate_room_agnostic() {
    file=$1
    # Nothing may consult room membership...
    ! grep -Fq 'isActiveWatchPartyItem' "$file" \
        && ! grep -Fq 'isWatchPartyMedia' "$file" \
        && ! grep -Fq 'isGuardedWatchPartyPath' "$file" \
        && ! grep -Fq 'watchPartyItemCache' "$file" \
        && ! grep -Fq '/emby/WatchParty/List' "$file" \
        && ! grep -Fq 'codex-emby-watchparty-routing-v' "$file" \
        `# ...while Xiaoya's own resolution path stays intact.` \
        && grep -Fq 'async function getPlaybackPath(' "$file" \
        && grep -Fq 'async function redirect2Pan(' "$file" \
        && grep -Fq 'getCachedXYUrl(alistFilePath' "$file" \
        && grep -Fq 'getCachedXYUrl(alistNextPath' "$file" \
        && grep -Fq '/stream.strm' "$file" \
        && grep -Fq 'doesNotContainHttp && doesNotContainDOCKER' "$file" \
        && grep -Fq 'r.internalRedirect("@backend")' "$file"
}

reload_nginx() {
    if [ "$reload" -eq 1 ] && [ -s /run/nginx/nginx.pid ]; then
        "$nginx_bin" -s reload
    fi
}

if validate_room_agnostic "$njs_script"; then
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

if ! awk '
    BEGIN {
        drop_helper = 0
        drop_guard = 0
        in_wrapper = 0
        wrapper_depth = 0
        pending_blank = 0
        removed = 0
    }

    # A blank line that only separated a removed block goes with it.
    pending_blank {
        pending_blank = 0
        if ($0 ~ /^[[:space:]]*$/) next
    }

    # The room lookup helper and its cache.
    /^var watchPartyItemCache =/ { drop_helper = 1; removed++; next }
    drop_helper {
        if ($0 ~ /^\}[[:space:]]*$/) { drop_helper = 0; pending_blank = 1 }
        next
    }

    # The routing block that preferred the backend for an active room.
    /codex-emby-watchparty-routing-v/ { drop_guard = 1; removed++; next }
    drop_guard {
        if ($0 ~ /^    \}[[:space:]]*$/) { drop_guard = 0; pending_blank = 1 }
        next
    }

    # Every call into the helper.
    /^[[:space:]]*var isWatchPartyMedia = await isActiveWatchPartyItem\(/ {
        removed++
        next
    }

    # Unwrap the bodies the helper used to gate, restoring Xiaoya native behavior.
    !in_wrapper && /^[[:space:]]*if \(!isWatchPartyMedia\) \{[[:space:]]*$/ {
        in_wrapper = 1
        wrapper_depth = 1
        removed++
        next
    }
    in_wrapper {
        line = $0
        # Only a whole-line comment can be ignored; a URL inside a string literal
        # carries "//" without starting one.
        if (line ~ /^[[:space:]]*\/\//) line = ""
        opens = gsub(/\{/, "", line)
        closes = gsub(/\}/, "", line)
        if (wrapper_depth + opens - closes <= 0) {
            in_wrapper = 0
            if ($0 ~ /^[[:space:]]*\}[[:space:]]*$/) next
            print
            next
        }
        wrapper_depth += opens - closes
        print
        next
    }

    { print }

    END {
        # An unbalanced unwrap would silently corrupt the script.
        if (removed < 1 || drop_helper || drop_guard || in_wrapper) exit 3
    }
' "$njs_script" >"$temporary_script"; then
    echo "could not remove the room-aware routing patch from $njs_script" >&2
    exit 1
fi

if ! validate_room_agnostic "$temporary_script"; then
    echo "Xiaoya emby.js still routes by Watch Party membership" >&2
    exit 1
fi

if [ "$(awk '{ o += gsub(/\{/, ""); c += gsub(/\}/, "") } END { print o - c }' \
        "$temporary_script")" \
    -ne "$(awk '{ o += gsub(/\{/, ""); c += gsub(/\}/, "") } END { print o - c }' \
        "$backup_script")" ]; then
    echo "removing the room-aware routing patch unbalanced $njs_script" >&2
    exit 1
fi

if ! cp "$temporary_script" "$njs_script"; then
    echo "could not install the room-agnostic routing" >&2
    exit 1
fi

if ! "$nginx_bin" -t >/dev/null 2>&1; then
    echo "Nginx rejected the room-agnostic routing" >&2
    exit 1
fi

restore_needed=0
reload_nginx
echo "patched"
