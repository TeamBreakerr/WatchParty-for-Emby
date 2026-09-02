#!/bin/sh
set -eu

locations_file=${1:-/data/emby-115-locations.conf}
test_root=$(mktemp -d /tmp/emby-115-singleflight.XXXXXX)
mkdir -p "$test_root/logs"
port_seed=$((($$ % 1000) * 2))
proxy_port=$((24000 + port_seed))
upstream_port=$((proxy_port + 1))
nginx_started=0
mock_pid=
first_pid=
second_pid=

cleanup() {
    status=$?
    trap - EXIT HUP INT TERM
    set +e
    [ "$nginx_started" -eq 0 ] \
        || nginx -s stop -c "$test_root/nginx.conf" -p "$test_root" >/dev/null 2>&1
    for process_id in "$mock_pid" "$first_pid" "$second_pid"; do
        [ -z "$process_id" ] || kill "$process_id" 2>/dev/null || true
    done
    rm -rf "$test_root"
    exit "$status"
}
trap cleanup EXIT
trap 'exit 1' HUP INT TERM

if [ ! -r "$locations_file" ]; then
    echo "could not read proxy locations: $locations_file" >&2
    exit 2
fi

lock_timeout=$(awk '
    $1 == "proxy_cache_lock_timeout" {
        gsub(";", "", $2)
        print $2
    }
' "$locations_file" | sort -u)
lock_age=$(awk '
    $1 == "proxy_cache_lock_age" {
        gsub(";", "", $2)
        print $2
    }
' "$locations_file" | sort -u)
if [ -z "$lock_timeout" ] || [ -z "$lock_age" ] \
    || [ "$(printf '%s\n' "$lock_timeout" | wc -l)" -ne 1 ] \
    || [ "$(printf '%s\n' "$lock_age" | wc -l)" -ne 1 ]; then
    echo "proxy locations do not define one consistent cache-lock policy" >&2
    exit 2
fi

# The mock accepts exactly one connection and delays its response headers for
# two seconds. If Nginx opens a duplicate upstream while that fill is active,
# the second connection is refused and one of the two client requests fails.
(
    sleep 2
    printf 'HTTP/1.1 206 Partial Content\r\n'
    printf 'Content-Length: 1048576\r\n'
    printf 'Content-Range: bytes 0-1048575/2097152\r\n'
    printf 'Accept-Ranges: bytes\r\n'
    printf 'Connection: close\r\n\r\n'
    dd if=/dev/zero bs=1048576 count=1 2>/dev/null
) | nc -l -p "$upstream_port" -s 127.0.0.1 >"$test_root/upstream.request" &
mock_pid=$!

cat >"$test_root/nginx.conf" <<EOF
user root;
worker_processes 1;
pid $test_root/nginx.pid;
error_log $test_root/nginx.error.log notice;

events {}

http {
    access_log off;
    proxy_cache_path $test_root/cache
        levels=1:2 keys_zone=singleflight:1m max_size=8m inactive=1m use_temp_path=off;

    server {
        listen 127.0.0.1:$proxy_port;

        location /media {
            slice 1m;
            proxy_cache singleflight;
            proxy_cache_key "\$uri:\$slice_range";
            proxy_cache_lock on;
            proxy_cache_lock_timeout $lock_timeout;
            proxy_cache_lock_age $lock_age;
            proxy_cache_valid 206 1m;
            proxy_set_header Range \$slice_range;
            proxy_set_header Connection "";
            proxy_buffering on;
            add_header X-Slice-Cache \$upstream_cache_status always;
            proxy_pass http://127.0.0.1:$upstream_port;
        }
    }
}
EOF

nginx -t -c "$test_root/nginx.conf" -p "$test_root" >/dev/null
nginx -c "$test_root/nginx.conf" -p "$test_root"
nginx_started=1

curl -sS --max-time 10 -D "$test_root/first.headers" \
    -r 0-1048575 -o /dev/null -w '%{http_code}' \
    "http://127.0.0.1:$proxy_port/media" >"$test_root/first.status" &
first_pid=$!
sleep 0.2
curl -sS --max-time 10 -D "$test_root/second.headers" \
    -r 0-1048575 -o /dev/null -w '%{http_code}' \
    "http://127.0.0.1:$proxy_port/media" >"$test_root/second.status" &
second_pid=$!

wait "$first_pid"
first_exit=$?
wait "$second_pid"
second_exit=$?
wait "$mock_pid"
mock_exit=$?

first_status=$(cat "$test_root/first.status")
second_status=$(cat "$test_root/second.status")
first_cache=$(tr -d '\r' <"$test_root/first.headers" \
    | awk -F': ' '/^X-Slice-Cache:/ {print $2; exit}')
second_cache=$(tr -d '\r' <"$test_root/second.headers" \
    | awk -F': ' '/^X-Slice-Cache:/ {print $2; exit}')
upstream_requests=$(grep -c '^GET ' "$test_root/upstream.request" || true)

printf 'first=%s cache=%s exit=%s\n' "$first_status" "$first_cache" "$first_exit"
printf 'second=%s cache=%s exit=%s\n' "$second_status" "$second_cache" "$second_exit"
printf 'upstream_requests=%s mock_exit=%s\n' "$upstream_requests" "$mock_exit"

if [ "$first_exit" -ne 0 ] || [ "$second_exit" -ne 0 ] \
    || [ "$mock_exit" -ne 0 ]; then
    echo "slow same-slice test process failed" >&2
    exit 1
fi
if [ "$first_status" != "206" ] || [ "$second_status" != "206" ]; then
    echo "slow same-slice requests did not both return 206" >&2
    exit 1
fi
if [ "$first_cache" != "MISS" ] || [ "$second_cache" != "HIT" ]; then
    echo "slow same-slice requests were not coalesced as MISS then HIT" >&2
    exit 1
fi
if [ "$upstream_requests" -ne 1 ]; then
    echo "duplicate same-slice upstream requests: $upstream_requests" >&2
    exit 1
fi
