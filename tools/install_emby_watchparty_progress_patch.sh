#!/bin/sh
set -eu

container=${EMBY_CONTAINER:-emby}
state_dir=${EMBY_PROGRESS_PATCH_STATE_DIR:-/home/teambreaker/emby/watchparty-progress-patch}
script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
patcher=${EMBY_PROGRESS_PATCHER:-$script_dir/patch_emby_watchparty_progress.py}
module_path=/system/dashboard-ui/modules/common/playback/playbackmanager.js

if [ ! -f "$patcher" ]; then
    echo "missing patcher: $patcher" >&2
    exit 2
fi

if [ "$(docker inspect --format '{{.State.Running}}' "$container" 2>/dev/null || true)" != "true" ]; then
    echo "Emby container is not running: $container" >&2
    exit 2
fi

temp_dir=$(mktemp -d)
deployment_started=0
deployment_completed=0
backup_dir=""
cleanup() {
    status=$?
    trap - EXIT HUP INT TERM
    set +e
    if [ "$status" -ne 0 ] \
        && [ "$deployment_started" -eq 1 ] \
        && [ "$deployment_completed" -eq 0 ]; then
        if [ -n "$backup_dir" ] && [ -f "$backup_dir/playbackmanager.js" ]; then
            docker cp "$backup_dir/playbackmanager.js" "$container:$module_path.codex-restore" >/dev/null 2>&1 \
                && docker exec "$container" mv "$module_path.codex-restore" "$module_path" >/dev/null 2>&1
        fi
    fi
    rm -rf "$temp_dir"
    exit "$status"
}
trap cleanup EXIT
trap 'exit 1' HUP INT TERM

work_dir=$temp_dir/dashboard-ui/modules/common/playback
verify_dir=$temp_dir/verify-dashboard-ui/modules/common/playback
mkdir -p "$work_dir" "$verify_dir" "$state_dir/backups"

docker cp "$container:$module_path" "$work_dir/playbackmanager.js"
cp -p "$work_dir/playbackmanager.js" "$temp_dir/playbackmanager.original.js"

patch_output=$(python3 "$patcher" --dashboard-root "$temp_dir/dashboard-ui")
if [ "$patch_output" = "already-patched" ]; then
    echo "already-patched"
    exit 0
fi
if [ "$patch_output" != "patched" ]; then
    echo "unexpected patcher output: $patch_output" >&2
    exit 3
fi

timestamp=$(date -u +%Y%m%dT%H%M%SZ)
image_id=$(docker inspect --format '{{.Image}}' "$container")
image_short=$(printf '%s' "$image_id" | cut -c1-19 | tr ':/' '__')
backup_dir=$state_dir/backups/$timestamp-$image_short
mkdir -p "$backup_dir"
cp -p "$temp_dir/playbackmanager.original.js" "$backup_dir/playbackmanager.js"

deployment_started=1
if ! docker cp "$work_dir/playbackmanager.js" "$container:$module_path.codex-new" \
    || ! docker exec "$container" mv "$module_path.codex-new" "$module_path"; then
    exit 4
fi

if ! docker cp "$container:$module_path" "$verify_dir/playbackmanager.js"; then
    echo "could not read deployed playbackmanager.js for verification" >&2
    exit 4
fi
if ! verify_output=$(python3 "$patcher" --dashboard-root "$temp_dir/verify-dashboard-ui"); then
    echo "deployed playbackmanager.js failed compatibility verification" >&2
    exit 4
fi
if [ "$verify_output" != "already-patched" ]; then
    echo "deployed playbackmanager.js did not pass idempotency verification" >&2
    exit 4
fi

deployment_completed=1
echo "patched backup=$backup_dir"
