#!/bin/sh
# codex-emby-115-updateall-wrapper

/updateall.xiaoya-original "$@"
status=$?
if [ "$status" -ne 0 ]; then
    exit "$status"
fi

exec /data/install-emby-115-proxy.sh
