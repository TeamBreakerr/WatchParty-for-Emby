#!/bin/sh
set -eu

container=${EMBY_CONTAINER:-emby}
state_dir=${EMBY_WS_PATCH_STATE_DIR:-/home/teambreaker/emby/websocket-reconnect-patch}
script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
patcher=${EMBY_WS_PATCHER:-$script_dir/patch_emby_websocket_reconnect.py}
module_path=/system/dashboard-ui/modules/emby-apiclient

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
cleanup() {
    status=$?
    trap - EXIT HUP INT TERM
    set +e
    if [ "$status" -ne 0 ] \
        && [ "$deployment_started" -eq 1 ] \
        && [ "$deployment_completed" -eq 0 ]; then
        restore_originals || true
    fi
    rm -rf "$temp_dir"
    exit "$status"
}
trap cleanup EXIT
trap 'exit 1' HUP INT TERM

dashboard_root=$temp_dir/dashboard-ui
work_module=$dashboard_root/modules/emby-apiclient
original_module=$temp_dir/original
verify_root=$temp_dir/verify-dashboard-ui
verify_module=$verify_root/modules/emby-apiclient
mkdir -p "$work_module" "$original_module" "$verify_module" "$state_dir/backups"

docker cp "$container:$module_path/apiclient.js" "$work_module/apiclient.js"
docker cp "$container:$module_path/connectionmanager.js" "$work_module/connectionmanager.js"
cp -p "$work_module/apiclient.js" "$original_module/apiclient.js"
cp -p "$work_module/connectionmanager.js" "$original_module/connectionmanager.js"

patch_output=$(python3 "$patcher" --dashboard-root "$dashboard_root")
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
cp -p "$original_module/apiclient.js" "$backup_dir/apiclient.js"
cp -p "$original_module/connectionmanager.js" "$backup_dir/connectionmanager.js"

restore_originals() {
    restore_failed=0
    api_restore_ready=0
    connection_restore_ready=0
    echo "deployment failed; restoring original Emby dashboard files" >&2
    if docker cp "$original_module/apiclient.js" "$container:$module_path/apiclient.js.codex-restore"; then
        api_restore_ready=1
    else
        restore_failed=1
    fi
    if docker cp "$original_module/connectionmanager.js" "$container:$module_path/connectionmanager.js.codex-restore"; then
        connection_restore_ready=1
    else
        restore_failed=1
    fi
    if [ "$api_restore_ready" -eq 1 ] \
        && ! docker exec "$container" mv "$module_path/apiclient.js.codex-restore" "$module_path/apiclient.js"; then
        restore_failed=1
    fi
    if [ "$connection_restore_ready" -eq 1 ] \
        && ! docker exec "$container" mv "$module_path/connectionmanager.js.codex-restore" "$module_path/connectionmanager.js"; then
        restore_failed=1
    fi
    if [ "$restore_failed" -ne 0 ]; then
        echo "automatic restore was incomplete; persistent backup: $backup_dir" >&2
        return 1
    fi
}

deployment_started=1
if ! docker cp "$work_module/apiclient.js" "$container:$module_path/apiclient.js.codex-new" \
    || ! docker cp "$work_module/connectionmanager.js" "$container:$module_path/connectionmanager.js.codex-new" \
    || ! docker exec "$container" mv "$module_path/apiclient.js.codex-new" "$module_path/apiclient.js" \
    || ! docker exec "$container" mv "$module_path/connectionmanager.js.codex-new" "$module_path/connectionmanager.js"; then
    exit 4
fi

if ! docker cp "$container:$module_path/apiclient.js" "$verify_module/apiclient.js" \
    || ! docker cp "$container:$module_path/connectionmanager.js" "$verify_module/connectionmanager.js"; then
    echo "could not read deployed files for verification" >&2
    exit 4
fi
if ! verify_output=$(python3 "$patcher" --dashboard-root "$verify_root"); then
    echo "deployed files failed compatibility verification" >&2
    exit 4
fi
if [ "$verify_output" != "already-patched" ]; then
    echo "deployed files did not pass idempotency verification" >&2
    exit 4
fi

deployment_completed=1
echo "patched backup=$backup_dir"
