#!/bin/sh
set -u

media_url=${1:?usage: xiaoya115ProxyIntegration.sh MEDIA_URL}
test_dir=$(mktemp -d /tmp/xiaoya-115-proxy-test.XXXXXX)

cleanup() {
    rm -rf "$test_dir"
}
trap cleanup EXIT HUP INT TERM

timeout 8 curl -g -sS --limit-rate 128k \
    -D "$test_dir/first.headers" \
    -r 0-5242879 -o /dev/null -w '%{http_code}' \
    "$media_url" >"$test_dir/first.status" &
first_pid=$!

timeout 8 curl -g -sS --limit-rate 128k \
    -D "$test_dir/second.headers" \
    -r 5242880-10485759 -o /dev/null -w '%{http_code}' \
    "$media_url" >"$test_dir/second.status" &
second_pid=$!

ready=0
for _ in $(seq 1 100); do
    if grep -Fq 'X-Emby-115-Proxy: dynamic' "$test_dir/first.headers" 2>/dev/null \
        && grep -Fq 'X-Emby-115-Proxy: dynamic' "$test_dir/second.headers" 2>/dev/null; then
        ready=1
        break
    fi
    sleep 0.05
done

if [ "$ready" -ne 1 ]; then
    echo "the first two protected streams did not become active" >&2
    exit 1
fi

curl -g -sS -D "$test_dir/third.headers" \
    -r 10485760-11534335 -o /dev/null \
    -w 'third=%{http_code} bytes=%{size_download} time=%{time_total}\n' \
    "$media_url"
third_exit=$?

wait "$first_pid"
first_exit=$?
wait "$second_pid"
second_exit=$?

printf 'first=%s exit=%s\n' "$(cat "$test_dir/first.status")" "$first_exit"
printf 'second=%s exit=%s\n' "$(cat "$test_dir/second.status")" "$second_exit"
printf 'third_exit=%s\n' "$third_exit"
tr -d '\r' <"$test_dir/third.headers" \
    | sed -n '/^HTTP/p; /^X-Emby-115-Proxy:/p; /^X-Emby-115-Guard:/p'

if [ "$third_exit" -ne 0 ]; then
    exit "$third_exit"
fi

if ! grep -Fq '206' "$test_dir/third.headers"; then
    echo "third range did not return 206" >&2
    exit 1
fi

if ! grep -Fq 'X-Emby-115-Proxy: dynamic' "$test_dir/third.headers"; then
    echo "third range did not pass through the dynamic 115 proxy" >&2
    exit 1
fi

if ! grep -Fq 'X-Emby-115-Guard: active' "$test_dir/third.headers"; then
    echo "third range did not pass through the connection guard" >&2
    exit 1
fi
