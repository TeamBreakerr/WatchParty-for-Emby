#!/bin/sh
set -eu

# Xiaoya regenerates these files when its container is recreated.  A literal
# Emby container address becomes stale after that recreation, while Docker DNS
# keeps following the current container address.

njs_script=${EMBY_NJS_SCRIPT:-/etc/nginx/http.d/emby.js}
emby_config=${EMBY_NGINX_CONFIG:-/etc/nginx/http.d/emby.conf}
nginx_bin=${NGINX_BIN:-nginx}
reload=0

case "${1:-}" in
    "") ;;
    --reload) reload=1 ;;
    *)
        echo "usage: $0 [--reload]" >&2
        exit 2
        ;;
esac

for source_file in "$njs_script" "$emby_config"; do
    if [ ! -r "$source_file" ]; then
        echo "missing Xiaoya Emby runtime file: $source_file" >&2
        exit 1
    fi
done

validate_upstream() {
    grep -Fq "var EMBY_HOST = 'http://emby:6908';" "$njs_script" \
        && [ "$(grep -Fc "var EMBY_HOST = 'http://emby:6908';" "$njs_script")" -eq 1 ] \
        && awk '
            /proxy_pass[[:space:]]+http:\/\/[^;]+:6908;/ {
                found++
                if ($0 !~ /proxy_pass[[:space:]]+http:\/\/emby:6908;/) bad++
            }
            END { exit found > 0 && bad == 0 ? 0 : 1 }
        ' "$emby_config"
}

reload_nginx() {
    if [ "$reload" -eq 1 ] && [ -s /run/nginx/nginx.pid ]; then
        "$nginx_bin" -s reload
    fi
}

if validate_upstream; then
    "$nginx_bin" -t >/dev/null 2>&1
    reload_nginx
    echo "already-present"
    exit 0
fi

temporary_njs=$(mktemp)
temporary_config=$(mktemp)
backup_njs=$(mktemp)
backup_config=$(mktemp)
restore_needed=0
keep_backups=0

cleanup() {
    exit_status=$?
    trap - EXIT HUP INT TERM
    set +e
    if [ "$exit_status" -ne 0 ] && [ "$restore_needed" -eq 1 ]; then
        cp -p "$backup_njs" "$njs_script" || keep_backups=1
        cp -p "$backup_config" "$emby_config" || keep_backups=1
        if [ "$keep_backups" -eq 1 ]; then
            echo "could not fully restore Xiaoya Emby runtime files" >&2
        fi
    fi
    rm -f "$temporary_njs" "$temporary_config"
    if [ "$keep_backups" -eq 0 ]; then
        rm -f "$backup_njs" "$backup_config"
    else
        echo "runtime backups preserved at $backup_njs and $backup_config" >&2
    fi
    exit "$exit_status"
}
trap cleanup EXIT
trap 'exit 1' HUP INT TERM

cp -p "$njs_script" "$backup_njs"
cp -p "$emby_config" "$backup_config"
restore_needed=1

if ! awk '
    BEGIN { replaced = 0 }
    /^[[:space:]]*var EMBY_HOST[[:space:]]*=/ {
        print "var EMBY_HOST = '\''http://emby:6908'\'';"
        replaced++
        next
    }
    { print }
    END { if (replaced != 1) exit 3 }
' "$njs_script" >"$temporary_njs"; then
    echo "could not locate the Xiaoya EMBY_HOST declaration" >&2
    exit 1
fi

if ! awk '
    BEGIN { replaced = 0 }
    /proxy_pass[[:space:]]+http:\/\/[^;]+:6908;/ {
        sub(/proxy_pass[[:space:]]+http:\/\/[^;]+:6908;/,
            "proxy_pass http://emby:6908;")
        replaced++
    }
    { print }
    END { if (replaced < 1) exit 3 }
' "$emby_config" >"$temporary_config"; then
    echo "could not locate a Xiaoya Emby proxy_pass" >&2
    exit 1
fi

cp "$temporary_njs" "$njs_script"
cp "$temporary_config" "$emby_config"

if ! validate_upstream || ! "$nginx_bin" -t >/dev/null 2>&1; then
    echo "Nginx rejected the Docker-DNS Emby upstream" >&2
    exit 1
fi

restore_needed=0
reload_nginx
echo "patched"
