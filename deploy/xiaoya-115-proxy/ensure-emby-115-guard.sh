#!/bin/sh
set -eu

listen_address=127.0.0.1:15678
health_url=http://127.0.0.1:15678/healthz
guard_command="/data/emby-115-guard -listen $listen_address"
lock_dir=/tmp/emby-115-guard-start.lock

if curl -fsS --max-time 1 "$health_url" >/dev/null 2>&1; then
    exit 0
fi

if ! mkdir "$lock_dir" 2>/dev/null; then
    exit 0
fi
trap 'rmdir "$lock_dir" 2>/dev/null || true' EXIT HUP INT TERM

if pgrep -f "^$guard_command$" >/dev/null 2>&1; then
    echo "115 guard process exists but its health endpoint is unavailable" >&2
    exit 1
fi

nohup /data/emby-115-guard -listen "$listen_address" \
    >>/var/log/emby-115-guard.log 2>&1 &

for _ in $(seq 1 50); do
    if curl -fsS --max-time 1 "$health_url" >/dev/null 2>&1; then
        exit 0
    fi
    sleep 0.1
done

echo "115 guard did not become healthy" >&2
exit 1
