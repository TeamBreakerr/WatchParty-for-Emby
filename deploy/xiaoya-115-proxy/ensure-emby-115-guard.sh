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

guard_pids=$(pgrep -f "^$guard_command$" 2>/dev/null || true)
if [ -n "$guard_pids" ]; then
    echo "restarting unhealthy 115 guard process" >&2
    for guard_pid in $guard_pids; do
        kill "$guard_pid" 2>/dev/null || true
    done

    for _ in $(seq 1 20); do
        still_running=0
        for guard_pid in $guard_pids; do
            if kill -0 "$guard_pid" 2>/dev/null; then
                still_running=1
            fi
        done
        [ "$still_running" -eq 1 ] || break
        sleep 0.1
    done

    for guard_pid in $guard_pids; do
        if kill -0 "$guard_pid" 2>/dev/null; then
            kill -KILL "$guard_pid" 2>/dev/null || true
        fi
    done
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
