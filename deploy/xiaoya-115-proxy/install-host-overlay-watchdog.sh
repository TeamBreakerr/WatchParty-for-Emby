#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
unit_dir=${SYSTEMD_UNIT_DIR:-/etc/systemd/system}
systemctl_bin=${SYSTEMCTL_BIN:-systemctl}

for unit in watchparty-xiaoya-overlay.service watchparty-xiaoya-overlay.timer; do
    if [ ! -s "$script_dir/$unit" ]; then
        echo "missing $script_dir/$unit" >&2
        exit 1
    fi
    install -m 0644 "$script_dir/$unit" "$unit_dir/$unit"
done

"$systemctl_bin" daemon-reload
"$systemctl_bin" enable --now watchparty-xiaoya-overlay.timer
