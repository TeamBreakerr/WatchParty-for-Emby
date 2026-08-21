#!/bin/sh
set -eu

mycmd_file=${1:-/home/teambreaker/xiaoya/mycmd.txt}
hook_command='docker exec xiaoya /data/install-emby-115-proxy.sh --reload'
begin_marker='xiaoyakeeper-xiaoya-begin'
end_marker='xiaoyakeeper-xiaoya-end'

if ! grep -Fq "$begin_marker" "$mycmd_file" || ! grep -Fq "$end_marker" "$mycmd_file"; then
    echo "could not find Xiaoya keeper markers in $mycmd_file" >&2
    exit 1
fi

if grep -Fq "$hook_command" "$mycmd_file"; then
    exit 0
fi

backup_file=$(mktemp)
trap 'rm -f "$backup_file"' EXIT HUP INT TERM
cp -p "$mycmd_file" "$backup_file"

if ! sed -i "/^update_xiaoya$/a\\$hook_command" "$mycmd_file"; then
    cp -p "$backup_file" "$mycmd_file"
    echo "failed to install Xiaoya keeper hook" >&2
    exit 1
fi
