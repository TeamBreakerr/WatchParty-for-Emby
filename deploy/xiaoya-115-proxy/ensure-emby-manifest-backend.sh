#!/bin/sh
set -eu

# Xiaoya routes `/videos/*/master` and `/videos/*/live` through njs together
# with `/videos/*/original` and `/videos/*/stream`, and answers all of them
# with a direct-link 302.  Those two locations carry `.m3u8` manifests: the
# client is asking the server to produce a stream, so a redirect to the
# original file on the provider's CDN can never satisfy it.  Emby Web requests
# `master.m3u8` whenever it has to transcode - HEVC it cannot decode, or an ASS
# subtitle it needs burned in - and a redirected manifest makes its player fail
# and retry, which is how a single page can leave several transcode sessions
# behind.
#
# A manifest therefore always stays on Emby's backend, in or out of a room.
# Requests for the original file keep Xiaoya's native direct-link behavior.

njs_script=${EMBY_NJS_SCRIPT:-/etc/nginx/http.d/emby.js}
nginx_bin=${NGINX_BIN:-nginx}
marker=codex-emby-manifest-backend-v1
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

validate_manifest_route() {
    file=$1
    [ "$(grep -Fc "$marker" "$file")" -eq 1 ] \
        && grep -Fq 'var isServerRenderedStream = r.uri.indexOf(".m3u8") !== -1;' "$file" \
        && grep -Fq 'if (isServerRenderedStream) {' "$file" \
        && [ "$(grep -Fc "$anchor" "$file")" -eq 1 ]
}

reload_nginx() {
    if [ "$reload" -eq 1 ] && [ -s /run/nginx/nginx.pid ]; then
        "$nginx_bin" -s reload
    fi
}

if validate_manifest_route "$njs_script"; then
    "$nginx_bin" -t >/dev/null 2>&1
    reload_nginx
    echo "already-present"
    exit 0
fi

if grep -Fq "$marker" "$njs_script"; then
    echo "existing manifest routing patch is incomplete" >&2
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
        print indent "var isServerRenderedStream = r.uri.indexOf(\".m3u8\") !== -1;"
        print indent "if (isServerRenderedStream) {"
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

if ! validate_manifest_route "$temporary_script"; then
    echo "patched Xiaoya emby.js failed manifest routing validation" >&2
    exit 1
fi

if ! cp "$temporary_script" "$njs_script"; then
    echo "could not install the manifest routing patch" >&2
    exit 1
fi

if ! "$nginx_bin" -t >/dev/null 2>&1; then
    echo "Nginx rejected the manifest routing patch" >&2
    exit 1
fi

restore_needed=0
reload_nginx
echo "patched"
