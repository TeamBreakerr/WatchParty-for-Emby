#!/bin/sh
set -eu

target_arch=${1:-arm64}
output_path=${2:-/tmp/emby-115-guard}
script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)

cd "$script_dir/guard"
CGO_ENABLED=0 GOOS=linux GOARCH="$target_arch" \
    go build -trimpath -ldflags='-s -w' -o "$output_path" .
