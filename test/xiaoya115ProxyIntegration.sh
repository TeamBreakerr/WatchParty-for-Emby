#!/bin/sh
set -u

media_url=${1:?usage: xiaoya115ProxyIntegration.sh MEDIA_URL}
test_dir=$(mktemp -d /tmp/xiaoya-115-proxy-test.XXXXXX)
guard_metrics_url=${GUARD_METRICS_URL:-http://127.0.0.1:15678/metrics}

metric_value() {
    curl -fsS --max-time 1 "$guard_metrics_url" \
        | awk -v metric="$1" '$1 == metric { print $2 }'
}

cleanup() {
    rm -rf "$test_dir"
}
trap cleanup EXIT HUP INT TERM

before_replacements=$(metric_value emby_115_guard_replacements_total)
before_breaches=$(metric_value emby_115_guard_connection_limit_breaches_total)
if [ -z "$before_replacements" ] || [ -z "$before_breaches" ]; then
    echo "could not read guard metrics before the integration test" >&2
    exit 1
fi

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

curl -g -sS --max-time 4 -D "$test_dir/third.headers" \
    -r 10485760-11534335 -o /dev/null \
    -w '%{http_code} %{size_download} %{time_total}\n' \
    "$media_url" >"$test_dir/third.result"
third_exit=$?

wait "$first_pid"
first_exit=$?
wait "$second_pid"
second_exit=$?

printf 'first=%s exit=%s\n' "$(cat "$test_dir/first.status")" "$first_exit"
printf 'second=%s exit=%s\n' "$(cat "$test_dir/second.status")" "$second_exit"
printf 'third_exit=%s\n' "$third_exit"
read -r third_status third_bytes third_time <"$test_dir/third.result"
printf 'third=%s bytes=%s time=%s\n' "$third_status" "$third_bytes" "$third_time"
tr -d '\r' <"$test_dir/third.headers" \
    | sed -n '/^HTTP/p; /^X-Emby-115-Proxy:/p; /^X-Emby-115-Guard:/p'

if [ "$third_exit" -ne 0 ]; then
    exit "$third_exit"
fi

if [ "$third_status" != "206" ]; then
    echo "third range did not return 206" >&2
    exit 1
fi

if ! awk -v elapsed="$third_time" 'BEGIN { exit !(elapsed <= 3.0) }'; then
    echo "third seek exceeded the 3 second latency limit: $third_time" >&2
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

after_replacements=$(metric_value emby_115_guard_replacements_total)
after_breaches=$(metric_value emby_115_guard_connection_limit_breaches_total)
if [ "$after_breaches" -ne "$before_breaches" ]; then
    echo "connection limit breach counter increased: $before_breaches -> $after_breaches" >&2
    exit 1
fi
if [ "$after_replacements" -le "$before_replacements" ]; then
    echo "replacement counter did not increase: $before_replacements -> $after_replacements" >&2
    exit 1
fi
