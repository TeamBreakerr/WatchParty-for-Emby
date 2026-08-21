#!/bin/sh
# codex-dynamic-emby-115-updateall-wrapper
set -u

/updateall.xiaoya-original "$@"
status=$?
if [ "$status" -ne 0 ]; then
    exit "$status"
fi

exec /data/install-emby-115-proxy.sh --reload
