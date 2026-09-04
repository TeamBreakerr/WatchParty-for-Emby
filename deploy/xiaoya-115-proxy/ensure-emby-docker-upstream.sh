#!/bin/sh
set -eu

# Xiaoya regenerates these files when its container is recreated.  A literal
# Emby container address becomes stale after that recreation, while Docker DNS
# keeps following the current container address.  Current Xiaoya versions ship
# the name themselves as `set $emby http://emby:6908;` and `proxy_pass $emby;`;
# older ones wrote a literal address into each proxy_pass.  Accept both.
#
# njs resolves `ngx.fetch` host names through Nginx's own `resolver`, not
# through /etc/resolv.conf.  Xiaoya's generated `emby.conf` only declares
# public resolvers, which cannot answer for a Docker container name, so the
# Emby upstream name needs Docker's embedded DNS in every Emby server block.
# Without it every njs metadata lookup fails and `redirect2Pan` answers
# HTTP 500 with `error: emby_api fetch failed`.

njs_script=${EMBY_NJS_SCRIPT:-/etc/nginx/http.d/emby.js}
emby_config=${EMBY_NGINX_CONFIG:-/etc/nginx/http.d/emby.conf}
nginx_bin=${NGINX_BIN:-nginx}
resolver_marker='# watchparty-emby-docker-dns'
resolv_conf=${EMBY_RESOLV_CONF:-/etc/resolv.conf}
upstream_host=${EMBY_UPSTREAM_HOST:-emby}
# Docker writes its embedded resolver into every container's resolv.conf, and it
# has been 127.0.0.11 for as long as libnetwork has existed - but it is absent
# under host networking and need not be that address on another runtime. Take
# whatever this container was actually given, and only fall back to the
# well-known address when resolv.conf names none.
resolver_address=${EMBY_RESOLVER_ADDRESS:-}
if [ -z "$resolver_address" ] && [ -r "$resolv_conf" ]; then
    resolver_address=$(awk '$1 == "nameserver" { print $2; exit }' "$resolv_conf")
fi
resolver_address=${resolver_address:-127.0.0.11}
resolver_line="resolver $resolver_address valid=10s ipv6=off; $resolver_marker"
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

# Every Emby server block must carry a server-scope resolver; an http-scope
# public resolver cannot answer for the Docker DNS upstream name.
server_blocks_resolve_docker_dns() {
    awk '
        function code(line) { sub(/#.*$/, "", line); return line }
        /^server[[:space:]]*\{/ { blocks++; depth = 1; next }
        blocks == 0 { next }
        depth == 1 && /^[[:space:]]*resolver[[:space:]]/ { resolved[blocks] = 1 }
        {
            line = code($0)
            depth += gsub(/\{/, "", line)
            depth -= gsub(/\}/, "", line)
        }
        END {
            if (blocks == 0) exit 1
            for (block = 1; block <= blocks; block++) {
                if (!resolved[block]) exit 1
            }
            exit 0
        }
    ' "$1"
}

# The container's grep prints nothing for -c when there are no matches, so a
# numeric test on its output errors instead of comparing.
count_lines() {
    awk -v needle="$2" 'index($0, needle) > 0 { found++ } END { print found + 0 }' "$1"
}

# The Emby upstream must be named by Docker DNS, however this Xiaoya version
# spells it: through its own `set $emby` variable, or written into each
# proxy_pass. A literal container address is stale the moment Emby is recreated.
upstream_is_docker_dns() {
    awk '
        /set[[:space:]]+\$emby[[:space:]]+http:\/\/emby:6908;/ { named++ }
        /proxy_pass[[:space:]]+http:\/\/emby:6908;/ { named++ }
        /(set[[:space:]]+\$emby|proxy_pass)[[:space:]]+http:\/\/[^;$]+:6908;/ {
            if ($0 !~ /http:\/\/emby:6908;/) stale++
        }
        END { exit (named > 0 && stale == 0) ? 0 : 1 }
    ' "$1"
}

validate_upstream() {
    grep -Fq "var EMBY_HOST = 'http://emby:6908';" "$njs_script" \
        && [ "$(count_lines "$njs_script" "var EMBY_HOST = 'http://emby:6908';")" -eq 1 ] \
        && upstream_is_docker_dns "$emby_config" \
        && server_blocks_resolve_docker_dns "$emby_config"
}

# Advisory only: without any resolver a variable proxy_pass fails too, so a
# name that does not resolve is worth reporting but not worth refusing over.
warn_if_upstream_unresolvable() {
    command -v getent >/dev/null 2>&1 || return 0
    getent hosts "$upstream_host" >/dev/null 2>&1 && return 0
    echo "warning: $upstream_host does not resolve from this container;" \
        "the Docker DNS resolver may not be able to answer for it" >&2
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

warn_if_upstream_unresolvable

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

if ! awk -v resolver_line="$resolver_line" -v marker="$resolver_marker" '
    function code(line) { sub(/#.*$/, "", line); return line }

    # Pass one records which server blocks already declare their own
    # server-scope resolver, so a pre-existing one is never duplicated.
    NR == FNR {
        if ($0 ~ /^server[[:space:]]*\{/) { blocks++; depth = 1; next }
        if (blocks == 0) next
        if (depth == 1 && $0 ~ /^[[:space:]]*resolver[[:space:]]/ &&
            index($0, marker) == 0) {
            resolved[blocks] = 1
        }
        line = code($0)
        depth += gsub(/\{/, "", line)
        depth -= gsub(/\}/, "", line)
        next
    }

    index($0, marker) > 0 && /^[[:space:]]*resolver[[:space:]]/ { next }

    /^server[[:space:]]*\{/ {
        block++
        print
        if (!resolved[block]) {
            print "    " resolver_line
            installed++
        }
        replaced_block++
        next
    }

    /(set[[:space:]]+\$emby|proxy_pass)[[:space:]]+http:\/\/[^;$]+:6908;/ {
        sub(/http:\/\/[^;$]+:6908;/, "http://emby:6908;")
        replaced++
    }

    { print }

    END { if (replaced_block < 1) exit 3 }
' "$emby_config" "$emby_config" >"$temporary_config"; then
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
