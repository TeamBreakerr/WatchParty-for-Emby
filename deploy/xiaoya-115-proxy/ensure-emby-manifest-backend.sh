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
#
# A manifest that resumes at a non-zero position additionally refuses audio
# stream copy.  Emby seeks such a session with `-ss` in front of `-i`: the video
# is decoded (and filtered, when an ASS subtitle is burned in) and therefore
# lands exactly on the requested position, while a `-c:a copy` audio stream can
# only be emitted from the enclosing Matroska cluster - up to several seconds
# earlier.  The first segment then carries audio ahead of video, hls.js anchors
# the fragment on that earliest sample, and the video buffer ends up starting
# after the position the player seeks to.  Its `maxBufferHole` is 0.1s, so it
# refuses to skip the gap: `readyState` never passes HAVE_METADATA, no frame is
# ever decoded, and playback hangs on a black screen without reporting an error.
# Measured on `藤本树 S01E01` resuming at 3s: audio started 3.003s before video,
# the video buffer began at 6.003s while the player sat at 3s, and zero frames
# decoded until `AllowAudioStreamCopy=false` realigned them to 0.026s apart.
#
# The rule keys off the resume position alone, not off Emby's copy decision,
# which the request does not reveal.  A session that would have remuxed audio
# therefore re-encodes it; that costs a fraction of a core next to the video
# work such a session is already doing, and it is what every FLAC title on this
# server already does.

njs_script=${EMBY_NJS_SCRIPT:-/etc/nginx/http.d/emby.js}
nginx_bin=${NGINX_BIN:-nginx}
marker=codex-emby-manifest-backend-v2
legacy_marker=codex-emby-manifest-backend-v1
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

validate_manifest_route() {
    file=$1
    [ "$(count_lines "$file" "$marker")" -eq 1 ] \
        && [ "$(count_lines "$file" "$legacy_marker")" -eq 0 ] \
        && grep -Fq 'var isServerRenderedStream = r.uri.indexOf(".m3u8") !== -1;' "$file" \
        && grep -Fq 'if (isServerRenderedStream) {' "$file" \
        && grep -Fq 'if (resumesMidStream && !decidesAudioCopy) {' "$file" \
        && grep -Fq 'r.variables.args = manifestArgs + "&AllowAudioStreamCopy=false";' "$file" \
        && [ "$(count_lines "$file" "$anchor")" -eq 1 ]
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

# A v1 block is this script's own output, so it is removed by shape: from its
# marker through the closing brace at the same indent, plus the blank line the
# insertion added after it.  Anything else keeps the marker and fails the
# validation below, which rolls the file back untouched.
if ! awk -v marker="$marker" -v legacy="$legacy_marker" -v anchor="$anchor" '
    index($0, legacy) > 0 && dropping == 0 && dropped == 0 {
        dropping = 1
        dropped_indent = $0
        sub(/[^[:space:]].*$/, "", dropped_indent)
        next
    }

    dropping == 1 {
        if ($0 == dropped_indent "}") {
            dropping = 2
            dropped = 1
        }
        next
    }

    dropping == 2 {
        dropping = 0
        if ($0 == "") { next }
    }

    index($0, anchor) > 0 && !added {
        indent = $0
        sub(/[^[:space:]].*$/, "", indent)
        print indent "// " marker
        print indent "var isServerRenderedStream = r.uri.indexOf(\".m3u8\") !== -1;"
        print indent "if (isServerRenderedStream) {"
        print indent "    var manifestArgs = r.variables.args || \"\";"
        print indent "    var manifestFields = manifestArgs.split(\"&\");"
        print indent "    var resumesMidStream = false;"
        print indent "    var decidesAudioCopy = false;"
        print indent "    for (var i = 0; i < manifestFields.length; i++) {"
        print indent "        var manifestField = manifestFields[i].split(\"=\");"
        print indent "        var manifestKey = manifestField[0].toLowerCase();"
        print indent "        if (manifestKey === \"starttimeticks\") {"
        print indent "            resumesMidStream = Number(manifestField[1]) > 0;"
        print indent "        } else if (manifestKey === \"allowaudiostreamcopy\") {"
        print indent "            decidesAudioCopy = true;"
        print indent "        }"
        print indent "    }"
        print indent "    if (resumesMidStream && !decidesAudioCopy) {"
        print indent "        r.variables.args = manifestArgs + \"&AllowAudioStreamCopy=false\";"
        print indent "    }"
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
