import os
import re
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[1]
DEPLOY_ROOT = REPO_ROOT / "deploy" / "xiaoya-115-proxy"


class Xiaoya115ProxyTests(unittest.TestCase):
    def test_emby_njs_patch_routes_guarded_media_without_a_range_probe(self):
        patcher = (
            DEPLOY_ROOT / "ensure-emby-direct-link-fallback.sh"
        ).read_text()

        self.assertIn("isXiaoyaMediaPath", patcher)
        self.assertIn('r.return(302, embyRes)', patcher)
        self.assertNotIn('print "                \\"Range\\": \\"bytes=0-0\\""', patcher)
        self.assertIn("r.internalRedirect(\"@backend\")", patcher)
        self.assertIn("getPlaybackPath", patcher)
        self.assertIn("drop_strm_preload", patcher)
        self.assertIn("PlaybackInfo?api_key=", patcher)
        self.assertIn("Nginx rejected the Emby direct-link fallback patch", patcher)

    def test_emby_njs_patch_is_idempotent_and_restores_on_nginx_failure(self):
        patcher = DEPLOY_ROOT / "ensure-emby-direct-link-fallback.sh"
        fixture = """async function fetchXYApi(xyurl, ua, cookie) {
    try {
        var res = await ngx.fetch(xyurl, {
            headers: {
                \"Content-Type\": 'application/json;charset=utf-8',
                \"User-Agent\": ua,
                \"X-Alist-OriUA\": ua
            },
            max_response_body_size: 65535
        });
        if (res.status >= 301 && res.status <= 307) {
            var loc = res.headers[\"Location\"] || res.headers[\"location\"];
            return loc || \"error: no location\";
        }
        var text = await res.text();
        try {
            var json = JSON.parse(text);
            if (json.url) return json.url;
            return text;
        } catch (e) {
            return text;
        }
    } catch (error) {
        return 'error: xy_api fetch failed';
    }
}

async function getPlaybackPath(itemId, userId, apiKey, r) {
    try {
        var strmUri = EMBY_HOST + '/emby/Videos/' + itemId + '/stream.strm?api_key=' + apiKey;
        var res = await ngx.fetch(strmUri, {
            max_response_body_size: 65535,
            headers: { 'X-Emby-Token': apiKey }
        });
        if (res.ok) {
            var content = await res.text();
            var url = content.trim();
            if (url && (url.startsWith('http://') || url.startsWith('https://'))) {
                return url;
            }
            if (url && url.includes('DOCKER_ADDRESS')) {
                return url;
            }
        }
    } catch (e) {}

    try {
        var playInfoUri = EMBY_HOST + '/emby/Items/' + itemId + '/PlaybackInfo?api_key=' + apiKey;
        var res = await ngx.fetch(playInfoUri, { max_response_body_size: 65535 });
        if (res.ok) {
            var data = await res.json();
            if (data && data.MediaSources && data.MediaSources.length > 0) {
                var mediaPath = data.MediaSources[0].Path;
                if (mediaPath) {
                    return mediaPath;
                }
            }
        }
    } catch (e) {}
    return null;
}

async function getCachedXYUrl(url, ua, itemId, cookie, r) {
    var cacheKey = getCacheKey(url, ua, itemId);
    var cached = getFromCache(cacheKey, r);
    if (cached) {
        return cached;
    }
    var result = await fetchXYApi(url, ua, cookie);
    if (!result.startsWith('error')) {
        setToCache(cacheKey, result, r);
    }
    return result;
}

async function redirect2Pan(r) {
    (async function() {
        var alistNextPath = nextPath.replace('DOCKER_ADDRESS', 'http://127.0.0.1:80') + '?sign=';
        await getCachedXYUrl(alistNextPath, ua, nextItemId, cookie, r);
    })();

    var contain115helper = embyRes.includes("P115StrmHelper");
    if (contain115helper) {
        var futureDiagnostic = "}";
        var helperRedirectUrl = await fetchXYApi(embyRes, ua, cookie);
        if (helperRedirectUrl.startsWith('error')) {
            r.internalRedirect("@backend");
            return;
        }
        r.return(302, helperRedirectUrl);
        return;
    }

    var alistFilePath = embyRes.replace('DOCKER_ADDRESS', 'http://127.0.0.1:80') + '?sign=';
    var alistRes = await getCachedXYUrl(alistFilePath, ua, itemId, cookie, r);

    if (!alistRes.startsWith('error')) {
        if (alistRes.indexOf(\"http\") !== -1) {
            r.return(302, alistRes);
            return;
        }
    }

    r.return(500, alistRes);
}
"""

        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            script = root / "emby.js"
            fake_nginx = root / "nginx"
            script.write_text(fixture)
            original = script.read_bytes()
            fake_nginx.write_text("#!/bin/sh\nexit 0\n")
            fake_nginx.chmod(0o755)
            env = {
                "PATH": "/usr/bin:/bin",
                "EMBY_NJS_SCRIPT": str(script),
                "NGINX_BIN": str(fake_nginx),
            }

            first = subprocess.run(
                [str(patcher)], capture_output=True, text=True, env=env
            )
            first_contents = script.read_bytes()
            second = subprocess.run(
                [str(patcher)], capture_output=True, text=True, env=env
            )

            self.assertEqual(0, first.returncode, first.stderr)
            self.assertEqual(0, second.returncode, second.stderr)
            self.assertNotEqual(original, first_contents)
            self.assertEqual(first_contents, script.read_bytes())
            updated = script.read_text()
            self.assertNotIn('"Range": "bytes=0-0"', updated)
            self.assertIn("var isXiaoyaMediaPath =", updated)
            self.assertIn('r.return(302, embyRes);', updated)
            self.assertNotIn(
                "var helperRedirectUrl = await fetchXYApi",
                updated,
            )
            self.assertNotIn("futureDiagnostic", updated)
            self.assertNotIn(
                "getCachedXYUrl(alistFilePath",
                updated,
            )
            self.assertNotIn(
                "getCachedXYUrl(alistNextPath",
                updated,
            )
            self.assertNotIn("/stream.strm", updated)

            legacy = fixture.replace(
                "async function fetchXYApi(xyurl, ua, cookie) {",
                "async function fetchXYApi(xyurl, ua, cookie) {\n"
                "    // codex-emby-direct-link-fallback-v1",
                1,
            ).replace(
                '                "X-Alist-OriUA": ua',
                '                "X-Alist-OriUA": ua,\n'
                '                "Range": "bytes=0-0"',
                1,
            ).replace(
                "        var text = await res.text();",
                "        if (res.status === 206 || "
                'res.headers["X-Emby-115-Proxy"] || '
                'res.headers["x-emby-115-proxy"]) {\n'
                '            return "error: media_body";\n'
                "        }\n"
                "        var text = await res.text();",
                1,
            ).replace(
                "    r.return(500, alistRes);",
                '    r.internalRedirect("@backend");',
                1,
            )
            script.write_text(legacy)
            migrated = subprocess.run(
                [str(patcher)], capture_output=True, text=True, env=env
            )
            migrated_contents = script.read_text()

            self.assertEqual(0, migrated.returncode, migrated.stderr)
            self.assertIn("codex-emby-guarded-path-routing-v2", migrated_contents)
            self.assertNotIn("codex-emby-direct-link-fallback-v1", migrated_contents)
            self.assertNotIn('"Range": "bytes=0-0"', migrated_contents)
            self.assertNotIn("error: media_body", migrated_contents)

            unrelated_probe = fixture.replace(
                "async function getPlaybackPath(itemId, userId, apiKey, r) {",
                "async function unrelatedMetadataProbe() {\n"
                '    return { "Range": "bytes=0-0" };\n'
                "}\n\n"
                "async function getPlaybackPath(itemId, userId, apiKey, r) {",
                1,
            )
            script.write_text(unrelated_probe)
            unrelated_result = subprocess.run(
                [str(patcher)], capture_output=True, text=True, env=env
            )

            self.assertEqual(0, unrelated_result.returncode, unrelated_result.stderr)
            self.assertIn(
                'return { "Range": "bytes=0-0" };',
                script.read_text(),
            )

            changed_fallback = fixture.replace(
                "r.return(500, alistRes);",
                "r.return(502, alistRes);",
                1,
            )
            script.write_text(changed_fallback)
            changed_result = subprocess.run(
                [str(patcher)], capture_output=True, text=True, env=env
            )

            self.assertEqual(0, changed_result.returncode, changed_result.stderr)
            self.assertNotIn("r.return(502, alistRes);", script.read_text())

            script.write_text(fixture)
            fake_nginx.write_text("#!/bin/sh\nexit 1\n")
            failed = subprocess.run(
                [str(patcher)], capture_output=True, text=True, env=env
            )
            self.assertNotEqual(0, failed.returncode)
            self.assertEqual(original, script.read_bytes())

    def test_proxy_is_selected_by_resolved_115_host_instead_of_folder_name(self):
        locations = (DEPLOY_ROOT / "emby-115-locations.conf").read_text()
        access = (DEPLOY_ROOT / "emby-115-access.lua").read_text()
        policy = (DEPLOY_ROOT / "emby_115_policy.lua").read_text()
        routing = locations + access + policy

        self.assertIn("/__emby_115_resolve/", locations)
        self.assertIn("resolve_115_target", access)
        self.assertIn("get_115_target_host", policy)
        self.assertIn("115cdn.net", policy)
        self.assertNotIn("合集（115）", routing)
        self.assertNotIn("我的115分享", routing)
        self.assertNotIn("Bangumi", routing)
        self.assertNotRegex(routing.lower(), r"[.]mkv|[.]mp4")

    def test_seek_guard_waits_for_natural_release_before_connecting(self):
        guard = (DEPLOY_ROOT / "guard" / "main.go").read_text()
        locations = (DEPLOY_ROOT / "emby-115-locations.conf").read_text()

        self.assertIn("newLeaseManager(2", guard)
        self.assertIn("waitForSlot", guard)
        self.assertIn("slotChanged", guard)
        self.assertNotIn("evicted.cancel()", guard)
        self.assertNotIn("replaced oldest upstream stream", guard)
        self.assertIn("errSlotWaitTimeout", guard)
        self.assertIn("emby_115_guard_slot_waits_total", guard)
        self.assertIn("emby_115_guard_slot_wait_timeouts_total", guard)
        self.assertIn("http.StatusServiceUnavailable", guard)
        self.assertIn("X-Emby-115-Target", locations)
        self.assertIn("rewrite ^ /stream break;", locations)
        self.assertIn("proxy_pass http://127.0.0.1:15678;", locations)
        stream_locations = locations.split("location @emby_115_stream", 1)[1]
        self.assertNotIn("body_filter_by_lua", stream_locations)

    def test_403_retry_is_bounded_and_refreshes_the_download_link(self):
        locations = (DEPLOY_ROOT / "emby-115-locations.conf").read_text()
        retry = (DEPLOY_ROOT / "emby-115-retry.lua").read_text()

        self.assertIn("error_page 403 503 = @emby_115_retry", locations)
        self.assertIn("X-Emby-115-Proxy dynamic", locations)
        self.assertIn('emby_115_attempt ~= "0"', retry)
        self.assertIn("resolve_115_target", retry)
        self.assertNotIn("error_page 403", locations.split("location @emby_115_retry", 1)[1])

    def test_probe_discards_the_first_body_chunk(self):
        locations = (DEPLOY_ROOT / "emby-115-locations.conf").read_text()
        resolver = locations.split("location ^~ /__emby_115_resolve/", 1)[1].split(
            "location @emby_115_stream", 1
        )[0]

        self.assertIn("body_filter_by_lua_block", resolver)
        self.assertIn("ngx.arg[1] = nil", resolver)
        self.assertIn("ngx.arg[2] = true", resolver)

    def test_go_and_lua_115_host_allowlists_are_identical(self):
        guard = (DEPLOY_ROOT / "guard" / "main.go").read_text()
        policy = (DEPLOY_ROOT / "emby_115_policy.lua").read_text()
        go_match = re.search(r"allowed115Suffixes = \[\]string\{([^}]*)\}", guard)
        lua_match = re.search(r"allowed_suffixes = \{([^}]*)\}", policy, re.DOTALL)

        self.assertIsNotNone(go_match)
        self.assertIsNotNone(lua_match)
        self.assertEqual(
            re.findall(r'"([^"]+)"', lua_match.group(1)),
            re.findall(r'"([^"]+)"', go_match.group(1)),
        )

    def test_health_check_restarts_an_unhealthy_existing_guard(self):
        ensure = (DEPLOY_ROOT / "ensure-emby-115-guard.sh").read_text()

        self.assertIn('guard_pids=$(pgrep -f "^$guard_command$"', ensure)
        self.assertIn('kill "$guard_pid"', ensure)
        self.assertIn('kill -KILL "$guard_pid"', ensure)
        self.assertNotIn("process exists but its health endpoint is unavailable", ensure)

    def test_installer_patches_only_includes_and_reinstalls_after_updates(self):
        installer = (DEPLOY_ROOT / "install-emby-115-proxy.sh").read_text()
        wrapper = (DEPLOY_ROOT / "updateall-emby-115-wrapper.sh").read_text()
        keeper = (DEPLOY_ROOT / "install-xiaoyakeeper-hook.sh").read_text()
        websocket_timeout = (
            DEPLOY_ROOT / "ensure-emby-websocket-timeout.sh"
        ).read_text()
        websocket_include = (
            DEPLOY_ROOT / "emby-websocket-timeout.conf"
        ).read_text()
        websocket_diagnostic = (
            DEPLOY_ROOT / "emby-websocket-diagnostic.conf"
        ).read_text()
        web_cache_buster = (
            DEPLOY_ROOT / "emby-web-cache-buster.conf"
        ).read_text()
        web_cache_ensurer = (
            DEPLOY_ROOT / "ensure-emby-web-cache-buster.sh"
        ).read_text()
        direct_link_ensurer = (
            DEPLOY_ROOT / "ensure-emby-direct-link-fallback.sh"
        ).read_text()
        retired_periodic_overlays = (
            "ensure-xiaoya-overlays.sh",
            "install-host-overlay-watchdog.sh",
            "watchparty-xiaoya-overlay.service",
            "watchparty-xiaoya-overlay.timer",
        )

        self.assertIn("include /data/emby-115-locations.conf;", installer)
        self.assertIn("include /data/emby-115-access.conf;", installer)
        self.assertIn('"$nginx_bin" -t', installer)
        self.assertIn("ensure-emby-115-guard.sh", installer)
        self.assertIn("ensure-emby-115-proxy.sh", installer)
        self.assertIn("/etc/crontabs/root", installer)
        self.assertIn("emby-websocket-timeout.conf", installer)
        self.assertIn("emby-websocket-diagnostic.conf", installer)
        self.assertIn("ensure-emby-websocket-timeout.sh", installer)
        self.assertIn("/data/ensure-emby-websocket-timeout.sh --reload", installer)
        self.assertIn("emby-web-cache-buster.conf", installer)
        self.assertIn("ensure-emby-web-cache-buster.sh", installer)
        self.assertIn('"$data_dir/ensure-emby-web-cache-buster.sh"', installer)
        self.assertIn("ensure-emby-direct-link-fallback.sh", installer)
        self.assertIn('"$data_dir/ensure-emby-direct-link-fallback.sh"', installer)
        self.assertIn("fetchXYApi", direct_link_ensurer)
        self.assertIn("data-appversion=\"4.9.0.42\"", web_cache_buster)
        self.assertIn("data-appversion=\"4.9.0.42-wp3\"", web_cache_buster)
        self.assertIn("/web/index.html", web_cache_ensurer)
        self.assertIn("validate_config", web_cache_ensurer)
        self.assertIn("Nginx rejected the Web cache-buster patch", web_cache_ensurer)
        self.assertIn("validate_config", websocket_timeout)
        self.assertIn("Nginx rejected the WebSocket patch", websocket_timeout)
        self.assertIn("proxy_read_timeout 86400s;", websocket_include)
        self.assertIn("proxy_socket_keepalive on;", websocket_include)
        self.assertIn("log_format emby_websocket_diagnostic", websocket_diagnostic)
        for retired_overlay in retired_periodic_overlays:
            self.assertFalse((DEPLOY_ROOT / retired_overlay).exists())
        self.assertIn("/updateall.xiaoya-original", wrapper)
        self.assertIn("install-emby-115-proxy-after-start.sh", keeper)
        self.assertIn("install-emby-115-proxy.sh --reload", keeper)
        self.assertIn("xiaoyakeeper-xiaoya-begin", keeper)

    def test_post_update_install_waits_for_fresh_services_then_installs_once(self):
        post_start = DEPLOY_ROOT / "install-emby-115-proxy-after-start.sh"

        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            pid_file = root / "nginx.pid"
            proc_root = root / "proc"
            config = root / "default.conf"
            curl_count = root / "curl-count"
            install_calls = root / "install-calls"
            fake_curl = root / "curl"
            fake_installer = root / "installer"

            pid_file.write_text(str(os.getpid()), encoding="utf-8")
            process_dir = proc_root / str(os.getpid())
            process_dir.mkdir(parents=True)
            (process_dir / "comm").write_text("nginx\n", encoding="utf-8")
            config.write_text("location /d/ { }\n", encoding="utf-8")
            fake_curl.write_text(
                "#!/bin/sh\n"
                f"count=$(cat '{curl_count}' 2>/dev/null || echo 0)\n"
                "count=$((count + 1))\n"
                f"echo \"$count\" >'{curl_count}'\n"
                "[ \"$count\" -ge 3 ]\n",
                encoding="utf-8",
            )
            fake_curl.chmod(0o755)
            fake_installer.write_text(
                "#!/bin/sh\n"
                f"printf '%s\\n' \"$*\" >>'{install_calls}'\n",
                encoding="utf-8",
            )
            fake_installer.chmod(0o755)

            env = os.environ.copy()
            env.update(
                {
                    "EMBY_115_NGINX_PID_FILE": str(pid_file),
                    "EMBY_115_PROC_ROOT": str(proc_root),
                    "EMBY_115_DEFAULT_CONFIG": str(config),
                    "EMBY_115_CURL_BIN": str(fake_curl),
                    "EMBY_115_INSTALLER": str(fake_installer),
                    "EMBY_115_STARTUP_WAIT_ATTEMPTS": "3",
                    "EMBY_115_STARTUP_WAIT_DELAY_SECONDS": "0",
                }
            )
            result = subprocess.run(
                [str(post_start)], capture_output=True, text=True, env=env
            )

            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual("3", curl_count.read_text(encoding="utf-8").strip())
            self.assertEqual(
                ["--reload"],
                install_calls.read_text(encoding="utf-8").splitlines(),
            )

    def test_installer_retries_reload_after_container_recreation(self):
        installer = DEPLOY_ROOT / "install-emby-115-proxy.sh"

        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            data_dir = root / "data"
            nginx_dir = root / "nginx"
            bin_dir = root / "bin"
            data_dir.mkdir()
            nginx_dir.mkdir()
            bin_dir.mkdir()

            default_config = nginx_dir / "default.conf"
            emby_config = nginx_dir / "emby.conf"
            runtime_config = nginx_dir / "emby-115-throttle.conf"
            cron_file = root / "root.cron"
            updateall = root / "updateall"
            pid_file = root / "nginx.pid"
            reload_count = root / "reload-count"

            default_config.write_text(
                "server {\n"
                "    include /data/emby-115-locations.conf;\n"
                "    location /d/ {\n"
                "        include /data/emby-115-access.conf;\n"
                "    }\n"
                "}\n",
                encoding="utf-8",
            )
            emby_config.write_text("server { listen 2345; }\n", encoding="utf-8")
            cron_file.write_text("", encoding="utf-8")
            updateall.write_text("#!/bin/sh\nexit 0\n", encoding="utf-8")
            updateall.chmod(0o755)
            pid_file.write_text(str(os.getpid()), encoding="utf-8")

            required_files = (
                "emby-115-access.conf",
                "emby-115-access.lua",
                "emby-115-locations.conf",
                "emby-115-retry.lua",
                "emby-115-throttle.conf",
                "emby-115-guard",
                "emby_115_policy.lua",
                "ensure-emby-115-guard.sh",
                "ensure-emby-115-proxy.sh",
                "install-emby-115-proxy-after-start.sh",
                "emby-websocket-diagnostic.conf",
                "emby-websocket-timeout.conf",
                "ensure-emby-websocket-timeout.sh",
                "emby-web-cache-buster.conf",
                "ensure-emby-web-cache-buster.sh",
                "ensure-emby-direct-link-fallback.sh",
                "updateall-emby-115-wrapper.sh",
            )
            executable_files = {
                "emby-115-guard",
                "ensure-emby-115-guard.sh",
                "ensure-emby-115-proxy.sh",
                "install-emby-115-proxy-after-start.sh",
                "ensure-emby-websocket-timeout.sh",
                "ensure-emby-web-cache-buster.sh",
                "ensure-emby-direct-link-fallback.sh",
            }
            for name in required_files:
                path = data_dir / name
                if name == "updateall-emby-115-wrapper.sh":
                    contents = "#!/bin/sh\n# codex-dynamic-emby-115-updateall-wrapper\n"
                elif name in executable_files:
                    contents = "#!/bin/sh\nexit 0\n"
                else:
                    contents = "fixture\n"
                path.write_text(contents, encoding="utf-8")
                if name in executable_files:
                    path.chmod(0o755)

            fake_nginx = bin_dir / "nginx"
            fake_nginx.write_text(
                "#!/bin/sh\n"
                "case \"${1:-}\" in\n"
                "  -t) exit 0 ;;\n"
                "  -T)\n"
                "    echo '# configuration file /data/emby-115-locations.conf:'\n"
                "    echo 'location @emby_115_stream {'\n"
                "    echo 'location @emby_115_retry {'\n"
                "    echo '# configuration file /data/emby-115-access.conf:'\n"
                "    echo 'access_by_lua_file /data/emby-115-access.lua;'\n"
                "    exit 0 ;;\n"
                "  -s)\n"
                f"    count=$(cat '{reload_count}' 2>/dev/null || echo 0)\n"
                "    count=$((count + 1))\n"
                f"    echo \"$count\" >'{reload_count}'\n"
                "    [ \"$count\" -ge 3 ] ;;\n"
                "  *) exit 2 ;;\n"
                "esac\n",
                encoding="utf-8",
            )
            fake_nginx.chmod(0o755)

            env = os.environ.copy()
            env.update(
                {
                    "EMBY_115_DATA_DIR": str(data_dir),
                    "EMBY_115_DEFAULT_CONFIG": str(default_config),
                    "EMBY_115_EMBY_CONFIG": str(emby_config),
                    "EMBY_115_RUNTIME_CONFIG": str(runtime_config),
                    "EMBY_115_CRON_FILE": str(cron_file),
                    "EMBY_115_UPDATEALL": str(updateall),
                    "EMBY_115_LEGACY_OVERLAY": str(data_dir / "legacy-overlay.sh"),
                    "EMBY_115_NGINX_BIN": str(fake_nginx),
                    "EMBY_115_NGINX_PID_FILE": str(pid_file),
                    "EMBY_115_RELOAD_ATTEMPTS": "3",
                    "EMBY_115_RELOAD_DELAY_SECONDS": "0",
                }
            )
            result = subprocess.run(
                [str(installer), "--reload"],
                capture_output=True,
                text=True,
                env=env,
            )

            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual("3", reload_count.read_text(encoding="utf-8").strip())
            installed = default_config.read_text(encoding="utf-8")
            self.assertIn("include /data/emby-115-locations.conf;", installed)
            self.assertIn("include /data/emby-115-access.conf;", installed)
            self.assertNotIn("restored Nginx configuration", result.stderr)

            reload_count.write_text("0\n", encoding="utf-8")
            env["EMBY_115_RELOAD_ATTEMPTS"] = "1"
            unavailable = subprocess.run(
                [str(installer), "--reload"],
                capture_output=True,
                text=True,
                env=env,
            )

            self.assertNotEqual(0, unavailable.returncode)
            self.assertEqual(installed, default_config.read_text(encoding="utf-8"))
            self.assertIn("installation remains active", unavailable.stderr)
            self.assertNotIn("restored Nginx configuration", unavailable.stderr)

    def test_health_check_repairs_missing_live_proxy_routes(self):
        ensure = DEPLOY_ROOT / "ensure-emby-115-proxy.sh"

        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            route_ready = root / "route-ready"
            install_calls = root / "install-calls"
            fake_nginx = root / "nginx"
            fake_guard_ensure = root / "ensure-guard"
            fake_installer = root / "install-proxy"

            fake_guard_ensure.write_text("#!/bin/sh\nexit 0\n", encoding="utf-8")
            fake_guard_ensure.chmod(0o755)
            fake_installer.write_text(
                "#!/bin/sh\n"
                f"echo called >'{install_calls}'\n"
                f"touch '{route_ready}'\n",
                encoding="utf-8",
            )
            fake_installer.chmod(0o755)
            fake_nginx.write_text(
                "#!/bin/sh\n"
                "[ \"${1:-}\" = '-T' ] || exit 2\n"
                "echo '# configuration file /etc/nginx/http.d/emby-115-throttle.conf:'\n"
                f"if [ -e '{route_ready}' ]; then\n"
                "  echo '# configuration file /data/emby-115-locations.conf:'\n"
                "  echo 'location @emby_115_stream {'\n"
                "  echo 'location @emby_115_retry {'\n"
                "  echo '# configuration file /data/emby-115-access.conf:'\n"
                "  echo 'access_by_lua_file /data/emby-115-access.lua;'\n"
                "fi\n",
                encoding="utf-8",
            )
            fake_nginx.chmod(0o755)

            env = os.environ.copy()
            env.update(
                {
                    "EMBY_115_NGINX_BIN": str(fake_nginx),
                    "EMBY_115_GUARD_ENSURE": str(fake_guard_ensure),
                    "EMBY_115_INSTALLER": str(fake_installer),
                    "EMBY_115_HEALTH_LOCK_DIR": str(root / "lock"),
                }
            )
            result = subprocess.run(
                [str(ensure)], capture_output=True, text=True, env=env
            )

            self.assertEqual(0, result.returncode, result.stderr)
            self.assertTrue(install_calls.exists())

    def test_installer_retires_the_previous_periodic_overlay(self):
        installer = (DEPLOY_ROOT / "install-emby-115-proxy.sh").read_text()
        readme = (DEPLOY_ROOT / "README.md").read_text()

        self.assertIn(
            "legacy_overlay_script=${EMBY_115_LEGACY_OVERLAY:-/data/ensure-xiaoya-overlays.sh}",
            installer,
        )
        self.assertIn(
            'sed -i "\\|$legacy_overlay_script|d" "$cron_file"', installer
        )
        self.assertIn('rm -f "$legacy_overlay_script"', installer)
        self.assertIn("install_committed=0", installer)
        self.assertIn(
            '[ "$status" -ne 0 ] && [ "$install_committed" -eq 0 ]', installer
        )
        self.assertLess(
            installer.rindex("install_committed=1"),
            installer.rindex("reload_nginx_when_ready"),
        )
        self.assertLess(
            installer.rindex("reload_nginx_when_ready"),
            installer.index('sed -i "\\|$legacy_overlay_script|d" "$cron_file"'),
        )
        self.assertNotIn("overlay_cron='* * * * *", installer)
        self.assertIn(
            "systemctl disable --now watchparty-xiaoya-overlay.timer", readme
        )
        self.assertIn("systemctl daemon-reload", readme)

    def test_integration_probe_models_two_streams_followed_by_a_seek(self):
        integration = (REPO_ROOT / "test" / "xiaoya115ProxyIntegration.sh").read_text()

        self.assertEqual(2, integration.count("--limit-rate 128k"))
        self.assertIn("third range did not return 206", integration)
        self.assertIn("third range completed before a natural slot release", integration)
        self.assertIn("third seek exceeded", integration)
        self.assertIn("connection limit breach counter increased", integration)
        self.assertIn("replacement counter increased", integration)
        self.assertIn("X-Emby-115-Proxy: dynamic", integration)
        self.assertIn("X-Emby-115-Guard: active", integration)

    @unittest.skipUnless(shutil.which("luajit"), "luajit is not installed")
    def test_lua_policy_vectors(self):
        subprocess.run(
            [
                "luajit",
                str(REPO_ROOT / "test" / "xiaoya115ProxyPolicy.test.lua"),
                str(REPO_ROOT),
            ],
            check=True,
        )


if __name__ == "__main__":
    unittest.main()
