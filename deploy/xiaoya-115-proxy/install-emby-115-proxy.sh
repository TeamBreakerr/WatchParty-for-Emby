#!/bin/sh
set -eu

default_config=/etc/nginx/http.d/default.conf
emby_config=/etc/nginx/http.d/emby.conf
runtime_config=/etc/nginx/http.d/emby-115-throttle.conf
server_include='        include /data/emby-115-locations.conf;'
access_include='            include /data/emby-115-access.conf;'
wrapper_marker='codex-dynamic-emby-115-updateall-wrapper'
default_backup=
emby_config_backup=
runtime_backup=
runtime_existed=0
updateall_backup=
cron_file=/etc/crontabs/root
cron_backup=
cron_existed=0
guard_cron='* * * * * /data/ensure-emby-115-guard.sh'
websocket_timeout_cron='* * * * * /data/ensure-emby-websocket-timeout.sh --reload'

cleanup() {
    status=$?
    trap - EXIT HUP INT TERM
    set +e
    if [ "$status" -ne 0 ]; then
        if [ -n "$default_backup" ]; then
            cp -p "$default_backup" "$default_config"
        fi
        if [ -n "$emby_config_backup" ]; then
            cp -p "$emby_config_backup" "$emby_config"
        fi
        if [ "$runtime_existed" -eq 1 ] && [ -n "$runtime_backup" ]; then
            cp -p "$runtime_backup" "$runtime_config"
        elif [ "$runtime_existed" -eq 0 ]; then
            rm -f "$runtime_config"
        fi
        if [ -n "$updateall_backup" ]; then
            cp -p "$updateall_backup" /updateall
        fi
        if [ "$cron_existed" -eq 1 ] && [ -n "$cron_backup" ]; then
            cp -p "$cron_backup" "$cron_file"
        elif [ "$cron_existed" -eq 0 ] && [ -n "$cron_backup" ]; then
            rm -f "$cron_file"
        fi
        echo "restored Nginx configuration after failed 115 proxy installation" >&2
    fi
    [ -z "$default_backup" ] || rm -f "$default_backup"
    [ -z "$emby_config_backup" ] || rm -f "$emby_config_backup"
    [ -z "$runtime_backup" ] || rm -f "$runtime_backup"
    [ -z "$updateall_backup" ] || rm -f "$updateall_backup"
    [ -z "$cron_backup" ] || rm -f "$cron_backup"
    exit "$status"
}
trap cleanup EXIT
trap 'exit 1' HUP INT TERM

required_files='emby-115-access.conf
emby-115-access.lua
emby-115-locations.conf
emby-115-retry.lua
emby-115-throttle.conf
emby-115-guard
emby_115_policy.lua
ensure-emby-115-guard.sh
emby-websocket-timeout.conf
ensure-emby-websocket-timeout.sh
updateall-emby-115-wrapper.sh'
for required_file in $required_files; do
    if [ ! -s "/data/$required_file" ]; then
        echo "missing /data/$required_file" >&2
        exit 1
    fi
done

if [ ! -x /data/emby-115-guard ] \
    || [ ! -x /data/ensure-emby-115-guard.sh ] \
    || [ ! -x /data/ensure-emby-websocket-timeout.sh ]; then
    echo "115 guard executables are not executable" >&2
    exit 1
fi

if ! grep -Fq 'location /d/ {' "$default_config"; then
    echo "could not find the official /d/ location in $default_config" >&2
    exit 1
fi

default_backup=$(mktemp)
cp -p "$default_config" "$default_backup"
emby_config_backup=$(mktemp)
cp -p "$emby_config" "$emby_config_backup"
if [ -e "$runtime_config" ]; then
    runtime_existed=1
    runtime_backup=$(mktemp)
    cp -p "$runtime_config" "$runtime_backup"
fi

if ! grep -Fq '/data/emby-115-locations.conf' "$default_config"; then
    sed -i "/^[[:space:]]*location \/d\/ {/i\\$server_include" "$default_config"
fi

if ! grep -Fq '/data/emby-115-access.conf' "$default_config"; then
    sed -i "/^[[:space:]]*location \/d\/ {/a\\$access_include" "$default_config"
fi

cp -p /data/emby-115-throttle.conf "$runtime_config"

cron_backup=$(mktemp)
if [ -e "$cron_file" ]; then
    cron_existed=1
    cp -p "$cron_file" "$cron_backup"
else
    : >"$cron_backup"
    : >"$cron_file"
fi
if ! grep -Fq '/data/ensure-emby-115-guard.sh' "$cron_file"; then
    printf '%s\n' "$guard_cron" >>"$cron_file"
fi
if ! grep -Fq '/data/ensure-emby-websocket-timeout.sh' "$cron_file"; then
    printf '%s\n' "$websocket_timeout_cron" >>"$cron_file"
fi

/data/ensure-emby-115-guard.sh
/data/ensure-emby-websocket-timeout.sh

nginx -t

if ! grep -Fq "$wrapper_marker" /updateall; then
    updateall_backup=$(mktemp)
    cp -p /updateall "$updateall_backup"
    cp -p /data/updateall-emby-115-wrapper.sh /updateall.codex-new
    chmod 755 /updateall.codex-new
    if [ ! -e /updateall.xiaoya-original ]; then
        mv /updateall /updateall.xiaoya-original
    fi
    mv /updateall.codex-new /updateall
fi

if [ "${1:-}" = "--reload" ]; then
    nginx -s reload
fi
