#!/bin/sh
set -eu

media_uri_file=${1:?usage: xiaoya115DirectRangeConcurrency.sh MEDIA_URI_FILE [ATTEMPTS]}
attempts=${2:-5}
resolver_origin=${XIAOYA_RESOLVER_ORIGIN:-http://127.0.0.1:5244}
guard_origin=${XIAOYA_GUARD_ORIGIN:-}
guard_key=0123456789abcdef0123456789abcdef
test_dir=$(mktemp -d /tmp/xiaoya-115-direct-range.XXXXXX)

cleanup() {
    jobs -p 2>/dev/null | xargs -r kill 2>/dev/null || true
    rm -rf "$test_dir"
}
trap cleanup EXIT HUP INT TERM

IFS= read -r media_uri <"$media_uri_file"
case "$media_uri" in
    /d/*) ;;
    *)
        echo "media URI must start with /d/" >&2
        exit 2
        ;;
esac

resolve_target() {
    response_headers=$1
    curl -g -sS --max-time 10 \
        -D "$response_headers" \
        -r 0-0 \
        -o /dev/null \
        "$resolver_origin$media_uri"
    awk '
        tolower($1) == "location:" {
            sub(/\r$/, "", $2)
            print $2
            exit
        }
    ' "$response_headers"
}

run_slow_range() {
    target=$1
    byte_range=$2
    status_file=$3
    headers_file=$4
    if [ -n "$guard_origin" ]; then
        curl -g -sS --limit-rate 128k --max-time 8 \
            -H "X-Emby-115-Target: $target" \
            -H "X-Emby-115-Key: $guard_key" \
            -D "$headers_file" \
            -r "$byte_range" \
            -o /dev/null \
            -w '%{http_code}\n' \
            "$guard_origin/stream" >"$status_file" 2>/dev/null || true
    else
        curl -g -sS --limit-rate 128k --max-time 8 \
            -D "$headers_file" \
            -r "$byte_range" \
            -o /dev/null \
            -w '%{http_code}\n' \
            "$target" >"$status_file" 2>/dev/null || true
    fi
}

attempt=1
accepted=0
rejected=0
while [ "$attempt" -le "$attempts" ]; do
    target=$(resolve_target "$test_dir/resolve-$attempt.headers")
    case "$target" in
        https://*.115cdn.net/*|https://115cdn.net/*|https://*.115cdn.com/*|https://115cdn.com/*|https://*.115.com/*|https://115.com/*) ;;
        *)
            echo "attempt $attempt: resolver did not return a supported 115 target" >&2
            exit 3
            ;;
    esac

    run_slow_range "$target" 0-8388607 \
        "$test_dir/first-$attempt.status" "$test_dir/first-$attempt.headers" &
    first_pid=$!
    run_slow_range "$target" 8388608-16777215 \
        "$test_dir/second-$attempt.status" "$test_dir/second-$attempt.headers" &
    second_pid=$!

    # Open the third request only after both CDN responses are confirmed as 206
    # and both deliberately throttled transfers are still active.
    baselines_active=0
    check=1
    while [ "$check" -le 100 ]; do
        if awk '$1 ~ /^HTTP\// && $2 == 206 { found = 1 } END { exit !found }' \
                "$test_dir/first-$attempt.headers" 2>/dev/null \
            && awk '$1 ~ /^HTTP\// && $2 == 206 { found = 1 } END { exit !found }' \
                "$test_dir/second-$attempt.headers" 2>/dev/null \
            && kill -0 "$first_pid" 2>/dev/null \
            && kill -0 "$second_pid" 2>/dev/null; then
            baselines_active=1
            break
        fi
        sleep 0.05
        check=$((check + 1))
    done
    if [ "$baselines_active" -ne 1 ]; then
        echo "attempt $attempt: two concurrent baseline Ranges did not become active" >&2
        exit 4
    fi
    if [ -n "$guard_origin" ]; then
        third_result=$(curl -g -sS --max-time 10 \
            -H "X-Emby-115-Target: $target" \
            -H "X-Emby-115-Key: $guard_key" \
            -D "$test_dir/third-$attempt.headers" \
            -r 16777216-17825791 \
            -o "$test_dir/third-$attempt.body" \
            -w '%{http_code} %{size_download} %{time_total}' \
            "$guard_origin/stream")
    else
        third_result=$(curl -g -sS --max-time 10 \
            -D "$test_dir/third-$attempt.headers" \
            -r 16777216-17825791 \
            -o "$test_dir/third-$attempt.body" \
            -w '%{http_code} %{size_download} %{time_total}' \
            "$target")
    fi
    set -- $third_result
    third_status=$1
    third_bytes=$2
    third_time=$3

    wait "$first_pid" || true
    wait "$second_pid" || true

    first_status=$(tail -1 "$test_dir/first-$attempt.status" 2>/dev/null || true)
    second_status=$(tail -1 "$test_dir/second-$attempt.status" 2>/dev/null || true)
    response_server=$(awk '
        tolower($1) == "server:" {
            sub(/\r$/, "", $2)
            print $2
            exit
        }
    ' "$test_dir/third-$attempt.headers")
    response_guard=$(awk '
        tolower($1) == "x-emby-115-guard:" {
            sub(/\r$/, "", $2)
            print $2
            exit
        }
    ' "$test_dir/third-$attempt.headers")
    printf 'attempt=%s first=%s second=%s third=%s bytes=%s time=%s server=%s guard=%s\n' \
        "$attempt" "${first_status:-none}" "${second_status:-none}" \
        "$third_status" "$third_bytes" "$third_time" \
        "${response_server:-unknown}" "${response_guard:-none}"

    if [ "$first_status" != 206 ] || [ "$second_status" != 206 ]; then
        echo "attempt $attempt: the two baseline Range requests were not both accepted" >&2
        exit 4
    fi
    if [ "$third_status" = 206 ] && [ "$third_bytes" = 1048576 ]; then
        accepted=$((accepted + 1))
    else
        rejected=$((rejected + 1))
    fi

    attempt=$((attempt + 1))
done

printf 'third_range_206=%s/%s rejected=%s\n' "$accepted" "$attempts" "$rejected"
if [ "$accepted" -ne "$attempts" ]; then
    exit 5
fi
