#!/bin/sh
set -eu

mycmd_file=${1:-/home/teambreaker/xiaoya/mycmd.txt}
hook_command='docker exec xiaoya /data/install-emby-115-proxy-after-start.sh'
legacy_hook_command='docker exec xiaoya /data/install-emby-115-proxy.sh --reload'
begin_marker='xiaoyakeeper-xiaoya-begin'
end_marker='xiaoyakeeper-xiaoya-end'

if ! grep -Fq "$begin_marker" "$mycmd_file" || ! grep -Fq "$end_marker" "$mycmd_file"; then
    echo "could not find Xiaoya keeper markers in $mycmd_file" >&2
    exit 1
fi

has_hook=0
has_legacy_hook=0
if grep -Fq "$hook_command" "$mycmd_file"; then
    has_hook=1
fi
if grep -Fq "$legacy_hook_command" "$mycmd_file"; then
    has_legacy_hook=1
fi
if [ "$has_hook" -eq 1 ] && [ "$has_legacy_hook" -eq 0 ]; then
    exit 0
fi

backup_file=$(mktemp)
trap 'rm -f "$backup_file"' EXIT HUP INT TERM
cp -p "$mycmd_file" "$backup_file"

if [ "$has_hook" -eq 1 ]; then
    update_command="\\|^$legacy_hook_command\$|d"
elif [ "$has_legacy_hook" -eq 1 ]; then
    update_command="s|^$legacy_hook_command\$|$hook_command|"
else
    update_command="/^update_xiaoya\$/a\\$hook_command"
fi

if ! sed -i "$update_command" "$mycmd_file"; then
    cp -p "$backup_file" "$mycmd_file"
    echo "failed to install Xiaoya keeper hook" >&2
    exit 1
fi
