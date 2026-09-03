import os
import re
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[1]
DEPLOY_ROOT = REPO_ROOT / "deploy" / "xiaoya-115-proxy"
UPSTREAM_FIXER = DEPLOY_ROOT / "ensure-emby-docker-upstream.sh"
OPENLIST_RESOLVER = DEPLOY_ROOT / "ensure-emby-openlist-resolver.sh"
RLIMIT_INSTALLER = DEPLOY_ROOT / "ensure-emby-nginx-rlimit.sh"
RLIMIT_CONFIG = DEPLOY_ROOT / "emby-nginx-rlimit.conf"
MANIFEST_ROUTER = DEPLOY_ROOT / "ensure-emby-manifest-backend.sh"

# Xiaoya's generated emby.js rewrites an Emby media path onto its own
# listener before asking OpenList for the signed direct link.  The dynamic
# 115 guard owns Xiaoya's `/d/` location and answers with the media body,
# so the resolution must address OpenList itself.
GUARDED_ORIGIN = "http://127.0.0.1:80"
OPENLIST_ORIGIN = "http://127.0.0.1:5244"
RESOLUTION_FIXTURE = """async function redirect2Pan(r) {
    var alistNextPath = nextPath.replace('DOCKER_ADDRESS', 'http://127.0.0.1:80').replace('http://172.19.0.1:5678', 'http://127.0.0.1:80').replace('http://xiaoya.host:5678', 'http://127.0.0.1:80') + '?sign=';
    var alistFilePath = embyRes.replace('DOCKER_ADDRESS', 'http://127.0.0.1:80').replace('http://172.19.0.1:5678', 'http://127.0.0.1:80').replace('http://xiaoya.host:5678', 'http://127.0.0.1:80') + '?sign=';
    return [alistFilePath, alistNextPath];
}
"""


def _resolver_env(script, nginx):
    return {
        "PATH": "/usr/bin:/bin",
        "EMBY_NJS_SCRIPT": str(script),
        "NGINX_BIN": str(nginx),
    }


def _write_fake_nginx(path, exit_code=0):
    path.write_text("#!/bin/sh\nexit %d\n" % exit_code)
    path.chmod(0o755)
    return path


class Xiaoya115ProxyTests(unittest.TestCase):
    def test_emby_runtime_uses_docker_dns_instead_of_a_stale_container_ip(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            emby_js = root / "emby.js"
            emby_conf = root / "emby.conf"
            fake_nginx = root / "nginx"
            emby_js.write_text("var EMBY_HOST = 'http://10.250.0.100:6908';\n")
            emby_conf.write_text(
                "server{\n"
                "    listen 2345;\n"
                "    location @backend {\n"
                "        proxy_pass http://10.250.0.100:6908;\n"
                "    }\n"
                "    location @transcode_backend {\n"
                "        proxy_pass http://emby:6908; # existing Docker DNS\n"
                "    }\n"
                "}\n"
            )
            fake_nginx.write_text("#!/bin/sh\nexit 0\n")
            fake_nginx.chmod(0o755)
            env = {
                "PATH": "/usr/bin:/bin",
                "EMBY_NJS_SCRIPT": str(emby_js),
                "EMBY_NGINX_CONFIG": str(emby_conf),
                "NGINX_BIN": str(fake_nginx),
            }

            first = subprocess.run(
                [str(UPSTREAM_FIXER)], capture_output=True, text=True, env=env
            )
            first_js = emby_js.read_bytes()
            first_conf = emby_conf.read_bytes()
            second = subprocess.run(
                [str(UPSTREAM_FIXER)], capture_output=True, text=True, env=env
            )

            self.assertEqual(0, first.returncode, first.stderr)
            self.assertEqual(0, second.returncode, second.stderr)
            self.assertEqual(first_js, emby_js.read_bytes())
            self.assertEqual(first_conf, emby_conf.read_bytes())
            self.assertIn("var EMBY_HOST = 'http://emby:6908';", emby_js.read_text())
            self.assertEqual(
                2,
                emby_conf.read_text().count("proxy_pass http://emby:6908;"),
            )
            self.assertNotIn("10.250.0.100", emby_js.read_text() + emby_conf.read_text())

    def test_emby_server_blocks_get_docker_dns_for_njs_lookups(self):
        # njs resolves `ngx.fetch` host names through Nginx's `resolver`, never
        # through /etc/resolv.conf.  A public resolver cannot answer for the
        # Docker DNS upstream name, so every njs metadata lookup would fail and
        # `redirect2Pan` would answer HTTP 500.
        regenerated = (
            "resolver 114.114.114.114 8.8.8.8 valid=1800s ipv6=off;\n"
            "\n"
            "server{\n"
            "    listen 2345;\n"
            "    location @backend {\n"
            "        proxy_pass http://10.250.0.99:6908;\n"
            "    }\n"
            "}\n"
            "\n"
            "server{\n"
            "    listen 2347;\n"
            "    # a commented brace { must not confuse block tracking\n"
            "    location / {\n"
            "        proxy_pass http://10.250.0.99:6908;\n"
            "    }\n"
            "}\n"
        )
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            emby_js = root / "emby.js"
            emby_conf = root / "emby.conf"
            fake_nginx = root / "nginx"
            emby_js.write_text("var EMBY_HOST = 'http://10.250.0.99:6908';\n")
            emby_conf.write_text(regenerated)
            fake_nginx.write_text("#!/bin/sh\nexit 0\n")
            fake_nginx.chmod(0o755)
            env = {
                "PATH": "/usr/bin:/bin",
                "EMBY_NJS_SCRIPT": str(emby_js),
                "EMBY_NGINX_CONFIG": str(emby_conf),
                "NGINX_BIN": str(fake_nginx),
            }

            first = subprocess.run(
                [str(UPSTREAM_FIXER)], capture_output=True, text=True, env=env
            )
            patched = emby_conf.read_text()
            second = subprocess.run(
                [str(UPSTREAM_FIXER)], capture_output=True, text=True, env=env
            )

            self.assertEqual(0, first.returncode, first.stderr)
            self.assertEqual("patched", first.stdout.strip())
            self.assertEqual("already-present", second.stdout.strip())
            self.assertEqual(patched, emby_conf.read_text())
            self.assertEqual(2, patched.count("resolver 127.0.0.11"))
            self.assertNotIn("10.250.0.99", patched)
            # The http-scope public resolver stays; only the Emby server blocks
            # gain Docker DNS.
            self.assertIn("resolver 114.114.114.114 8.8.8.8", patched)

    def test_an_existing_server_resolver_is_never_duplicated(self):
        existing = (
            "server{\n"
            "    resolver 10.0.0.53 valid=30s;\n"
            "    location @backend {\n"
            "        proxy_pass http://10.250.0.99:6908;\n"
            "    }\n"
            "}\n"
        )
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            emby_js = root / "emby.js"
            emby_conf = root / "emby.conf"
            fake_nginx = root / "nginx"
            emby_js.write_text("var EMBY_HOST = 'http://10.250.0.99:6908';\n")
            emby_conf.write_text(existing)
            fake_nginx.write_text("#!/bin/sh\nexit 0\n")
            fake_nginx.chmod(0o755)

            result = subprocess.run(
                [str(UPSTREAM_FIXER)],
                capture_output=True,
                text=True,
                env={
                    "PATH": "/usr/bin:/bin",
                    "EMBY_NJS_SCRIPT": str(emby_js),
                    "EMBY_NGINX_CONFIG": str(emby_conf),
                    "NGINX_BIN": str(fake_nginx),
                },
            )
            patched = emby_conf.read_text()

            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual(1, patched.count("resolver "))
            self.assertIn("resolver 10.0.0.53 valid=30s;", patched)
            self.assertIn("proxy_pass http://emby:6908;", patched)

    def test_emby_runtime_upstream_repair_restores_both_files_on_nginx_failure(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            emby_js = root / "emby.js"
            emby_conf = root / "emby.conf"
            fake_nginx = root / "nginx"
            emby_js.write_text("var EMBY_HOST = 'http://10.250.0.100:6908';\n")
            emby_conf.write_text(
                "location @backend {\n"
                "    proxy_pass http://10.250.0.100:6908;\n"
                "}\n"
            )
            original_js = emby_js.read_bytes()
            original_conf = emby_conf.read_bytes()
            fake_nginx.write_text("#!/bin/sh\nexit 1\n")
            fake_nginx.chmod(0o755)
            env = {
                "PATH": "/usr/bin:/bin",
                "EMBY_NJS_SCRIPT": str(emby_js),
                "EMBY_NGINX_CONFIG": str(emby_conf),
                "NGINX_BIN": str(fake_nginx),
            }

            result = subprocess.run(
                [str(UPSTREAM_FIXER)], capture_output=True, text=True, env=env
            )

            self.assertNotEqual(0, result.returncode)
            self.assertEqual(original_js, emby_js.read_bytes())
            self.assertEqual(original_conf, emby_conf.read_bytes())

    def test_emby_njs_patch_scopes_guarded_routing_to_active_room_items(self):
        patcher = (
            DEPLOY_ROOT / "ensure-emby-direct-link-fallback.sh"
        ).read_text()

        self.assertIn("isActiveWatchPartyItem", patcher)
        self.assertIn("/emby/WatchParty/List?api_key=", patcher)
        self.assertIn("party.IsActive", patcher)
        self.assertIn("party.CurrentEpisodeId", patcher)
        self.assertIn("isWatchPartyMedia && isXiaoyaMediaPath", patcher)
        self.assertIn("if (!isWatchPartyMedia)", patcher)

    def test_emby_njs_patch_routes_guarded_media_without_a_range_probe(self):
        patcher = (
            DEPLOY_ROOT / "ensure-emby-direct-link-fallback.sh"
        ).read_text()

        self.assertIn("isXiaoyaMediaPath", patcher)
        self.assertNotIn('print "                \\"Range\\": \\"bytes=0-0\\""', patcher)
        self.assertIn("r.internalRedirect(\"@backend\")", patcher)
        self.assertIn("getPlaybackPath", patcher)
        self.assertIn("print_native_stream_probe", patcher)
        self.assertIn("Nginx rejected the Emby direct-link fallback patch", patcher)

    def test_emby_njs_patch_keeps_local_media_on_the_emby_backend(self):
        patcher = (
            DEPLOY_ROOT / "ensure-emby-direct-link-fallback.sh"
        ).read_text()

        self.assertIn("isLocalMediaPath", patcher)
        self.assertIn("doesNotContainHttp && doesNotContainDOCKER", patcher)
        self.assertIn(
            'print "    if (isLocalMediaPath || isGuardedWatchPartyPath ||"',
            patcher,
        )
        self.assertIn(
            'print "        r.internalRedirect(\\"@backend\\");"',
            patcher,
        )

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
    var itemIdMatch = /\\/videos\\/(\\d+)/i.exec(r.uri);
    var itemId = itemIdMatch ? itemIdMatch[1] : null;
    var api_key = r.args.api_key || 'fixture-token';
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
            self.assertIn("codex-emby-watchparty-routing-v4", updated)
            self.assertIn("async function isActiveWatchPartyItem", updated)
            self.assertIn("var isLocalMediaPath =", updated)
            self.assertIn(
                "isWatchPartyMedia && isXiaoyaMediaPath",
                updated,
            )
            self.assertIn("var isXiaoyaMediaPath =", updated)
            self.assertIn(
                "var helperRedirectUrl = await fetchXYApi",
                updated,
            )
            self.assertIn("futureDiagnostic", updated)
            self.assertIn(
                "getCachedXYUrl(alistFilePath",
                updated,
            )
            self.assertIn(
                "getCachedXYUrl(alistNextPath",
                updated,
            )
            self.assertIn("/stream.strm", updated)

            helper_start = updated.index("var watchPartyItemCache")
            helper_end = updated.index(
                "async function getPlaybackPath", helper_start
            )
            helper = updated[helper_start:helper_end]
            node_result = subprocess.run(
                [
                    "node",
                    "-e",
                    "const EMBY_HOST='http://emby:6908';"
                    "const ngx={fetch:async function(){return {ok:true,json:async function(){"
                    "return {Parties:["
                    "{IsActive:true,ItemId:'root-1',CurrentEpisodeId:'episode-1'},"
                    "{IsActive:false,ItemId:'inactive',CurrentEpisodeId:'inactive-episode'}"
                    "]}}}}};"
                    + helper
                    + "Promise.all(['root-1','episode-1','inactive','missing'].map("
                    "function(id){return isActiveWatchPartyItem(id,'token')})).then("
                    "function(values){process.stdout.write(JSON.stringify(values))})",
                ],
                capture_output=True,
                text=True,
            )
            self.assertEqual(0, node_result.returncode, node_result.stderr)
            self.assertEqual("[true,true,false,false]", node_result.stdout)

            redirect_result = subprocess.run(
                [
                    "node",
                    "-e",
                    "var EMBY_HOST='http://emby:6908';"
                    "var nextPath='DOCKER_ADDRESS/next',ua='ua',nextItemId='1002',"
                    "cookie='',embyRes='DOCKER_ADDRESS/current',"
                    "doesNotContainHttp=false,doesNotContainDOCKER=false;"
                    "function getCacheKey(){return 'fixture'}"
                    "function getFromCache(){return null}"
                    "function setToCache(){}"
                    "var ngx={fetch:async function(uri){"
                    "if(String(uri).includes('WatchParty/List'))return {ok:true,json:async function(){"
                    "return {Parties:[{IsActive:true,ItemId:'1001'}]}}};"
                    "return {status:302,headers:{Location:'https://media.invalid/file'}}}};"
                    + updated
                    + "var activeActions=[],inactiveActions=[];"
                    "var active={uri:'/videos/1001/original.mkv',args:{api_key:'token'},"
                    "internalRedirect:function(value){activeActions.push(value)},"
                    "return:function(status){activeActions.push(status)}};"
                    "var inactive={uri:'/videos/1009/original.mkv',args:{api_key:'token'},"
                    "internalRedirect:function(value){inactiveActions.push(value)},"
                    "return:function(status){inactiveActions.push(status)}};"
                    "redirect2Pan(active).then(function(){return redirect2Pan(inactive)})"
                    ".then(function(){process.stdout.write(JSON.stringify([activeActions,inactiveActions]))})"
                    ".catch(function(error){console.error(error);process.exit(1)})",
                ],
                capture_output=True,
                text=True,
            )
            self.assertEqual(0, redirect_result.returncode, redirect_result.stderr)
            self.assertEqual('[["@backend"],[302]]', redirect_result.stdout)

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
            self.assertIn("codex-emby-watchparty-routing-v4", migrated_contents)
            self.assertNotIn("codex-emby-direct-link-fallback-v1", migrated_contents)
            self.assertNotIn('"Range": "bytes=0-0"', migrated_contents)
            self.assertNotIn("error: media_body", migrated_contents)
            self.assertIn("/stream.strm", migrated_contents)
            self.assertIn("getCachedXYUrl(alistFilePath", migrated_contents)

            playback_path_start = fixture.index(
                "async function getPlaybackPath(itemId, userId, apiKey, r) {"
            )
            first_probe_start = fixture.index("    try {", playback_path_start)
            playback_info_start = fixture.index("    try {", first_probe_start + 1)
            previous_v3 = fixture[:first_probe_start] + fixture[playback_info_start:]
            previous_v3 = previous_v3.replace(
                "        await getCachedXYUrl(alistNextPath, ua, nextItemId, cookie, r);",
                "        // Guarded media is resolved when playback starts.",
                1,
            )
            old_tail_start = previous_v3.index(
                "    var contain115helper = embyRes.includes"
            )
            previous_v3 = previous_v3[:old_tail_start] + """    var contain115helper = embyRes.includes("P115StrmHelper");
    if (contain115helper) {
        r.internalRedirect("@backend");
        return;
    }
    // codex-emby-guarded-path-routing-v3
    var isLocalMediaPath = doesNotContainHttp && doesNotContainDOCKER;
    var isXiaoyaMediaPath = embyRes.includes("DOCKER_ADDRESS") ||
        embyRes.includes("xiaoya.host:5678");
    if (isLocalMediaPath || isXiaoyaMediaPath) {
        r.internalRedirect("@backend");
        return;
    }
    r.return(302, embyRes);
}
"""
            script.write_text(previous_v3)
            upgraded_v3 = subprocess.run(
                [str(patcher)], capture_output=True, text=True, env=env
            )
            upgraded_v3_contents = script.read_text()

            self.assertEqual(0, upgraded_v3.returncode, upgraded_v3.stderr)
            self.assertIn(
                "codex-emby-watchparty-routing-v4",
                upgraded_v3_contents,
            )
            self.assertNotIn(
                "codex-emby-guarded-path-routing-v3",
                upgraded_v3_contents,
            )
            self.assertIn("/stream.strm", upgraded_v3_contents)
            self.assertIn("getCachedXYUrl(alistFilePath", upgraded_v3_contents)
            self.assertIn("getCachedXYUrl(alistNextPath", upgraded_v3_contents)

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
            self.assertIn("r.return(502, alistRes);", script.read_text())

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

    def test_seek_guard_uses_cached_slices_and_fifo_upstream_leases(self):
        guard = (DEPLOY_ROOT / "guard" / "main.go").read_text()
        locations = (DEPLOY_ROOT / "emby-115-locations.conf").read_text()
        throttle = (DEPLOY_ROOT / "emby-115-throttle.conf").read_text()

        self.assertIn("newLeaseManager(2", guard)
        self.assertIn("waiters", guard)
        self.assertIn("grantWaitingLocked", guard)
        self.assertNotIn("evicted.cancel()", guard)
        self.assertNotIn("replaced oldest upstream stream", guard)
        self.assertIn("errSlotWaitTimeout", guard)
        self.assertIn("emby_115_guard_slot_waits_total", guard)
        self.assertIn("emby_115_guard_slot_wait_timeouts_total", guard)
        self.assertIn("http.StatusServiceUnavailable", guard)
        self.assertIn("X-Emby-115-Target", locations)
        self.assertIn("rewrite ^ /stream break;", locations)
        self.assertIn("proxy_pass http://127.0.0.1:15678;", locations)
        self.assertIn("proxy_cache_path", throttle)
        self.assertIn("keys_zone=emby_115_slices", throttle)
        self.assertEqual(2, locations.count("slice 1m;"))
        self.assertEqual(2, locations.count("proxy_cache emby_115_slices;"))
        self.assertEqual(2, locations.count("proxy_cache_lock on;"))
        self.assertEqual(2, locations.count("proxy_cache_lock_timeout 70s;"))
        self.assertEqual(2, locations.count("proxy_cache_lock_age 70s;"))
        request_timeout = int(re.search(
            r"upstreamRequestTimeout\s*=\s*(\d+) \* time.Second", guard
        ).group(1))
        lease_wait = int(re.search(
            r"upstreamLeaseWait\s*=\s*(\d+) \* time.Second", guard
        ).group(1))
        close_grace_ms = int(re.search(
            r"upstreamCloseGrace\s*=\s*(\d+) \* time.Millisecond", guard
        ).group(1))
        lock_seconds = int(re.search(
            r"proxy_cache_lock_timeout (\d+)s;", locations
        ).group(1))
        self.assertGreater(
            lock_seconds,
            request_timeout + lease_wait + close_grace_ms / 1000,
        )
        self.assertIn("upstreamRequestTimeout = 60 * time.Second", guard)
        self.assertIn("Timeout: upstreamRequestTimeout", guard)
        self.assertEqual(2, locations.count("proxy_set_header Range $slice_range;"))
        self.assertEqual(
            2,
            locations.count(
                'proxy_cache_key "$emby_115_key:$slice_range";'
            ),
        )
        self.assertNotIn("$emby_115_target:$slice_range", locations)
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

    def test_resolver_and_stream_use_the_same_nonempty_user_agent(self):
        locations = (DEPLOY_ROOT / "emby-115-locations.conf").read_text()
        guard = (DEPLOY_ROOT / "guard" / "main.go").read_text()

        user_agent_match = re.search(
            r'set \$emby_115_user_agent "([^"]+)";', locations
        )
        guard_user_agent_match = re.search(
            r'stableUserAgent\s*=\s*"([^"]+)"', guard
        )

        self.assertIsNotNone(user_agent_match)
        self.assertIsNotNone(guard_user_agent_match)
        self.assertEqual(user_agent_match.group(1), guard_user_agent_match.group(1))
        self.assertNotEqual("", user_agent_match.group(1))
        self.assertEqual(
            3,
            locations.count("proxy_set_header User-Agent $emby_115_user_agent;"),
        )

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
        upstream_ensurer = (
            DEPLOY_ROOT / "ensure-emby-docker-upstream.sh"
        ).read_text()
        direct_link_ensurer = (
            DEPLOY_ROOT / "ensure-emby-direct-link-fallback.sh"
        ).read_text()
        runtime_installer = (
            DEPLOY_ROOT / "install-emby-115-runtime.sh"
        ).read_text()
        post_start_installer = (
            DEPLOY_ROOT / "install-emby-115-proxy-after-start.sh"
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
        self.assertIn("ensure-emby-docker-upstream.sh", installer)
        self.assertIn('"$data_dir/ensure-emby-docker-upstream.sh"', installer)
        self.assertIn('cp -p "$njs_script" "$njs_script_backup"', installer)
        self.assertIn('cp -p "$njs_script_backup" "$njs_script"', installer)
        self.assertIn("fetchXYApi", direct_link_ensurer)
        self.assertIn("http://emby:6908", upstream_ensurer)
        self.assertIn("data-appversion=\"4.9.0.42\"", web_cache_buster)
        self.assertIn("data-appversion=\"4.9.0.42-wp5\"", web_cache_buster)
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
        self.assertIn("install-emby-115-runtime.sh", post_start_installer)
        self.assertIn("emby-115-locations.conf", runtime_installer)
        self.assertIn("ensure-emby-115-guard.sh", runtime_installer)
        self.assertIn("ensure-emby-docker-upstream.sh", runtime_installer)
        self.assertNotIn("/etc/crontabs/root", runtime_installer)
        self.assertNotIn("/updateall", runtime_installer)
        self.assertIn("ensure-emby-direct-link-fallback", runtime_installer)
        self.assertNotIn("ensure-emby-websocket-timeout", runtime_installer)

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
            emby_js = nginx_dir / "emby.js"
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
            emby_js.write_text(
                "var EMBY_HOST = 'http://emby:6908';\n", encoding="utf-8"
            )
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
                "install-emby-115-runtime.sh",
                "install-emby-115-proxy-after-start.sh",
                "emby-websocket-diagnostic.conf",
                "emby-websocket-timeout.conf",
                "ensure-emby-websocket-timeout.sh",
                "emby-web-cache-buster.conf",
                "ensure-emby-web-cache-buster.sh",
                "ensure-emby-docker-upstream.sh",
                "ensure-emby-direct-link-fallback.sh",
                "ensure-emby-openlist-resolver.sh",
                "ensure-emby-manifest-backend.sh",
                "emby-nginx-rlimit.conf",
                "ensure-emby-nginx-rlimit.sh",
                "updateall-emby-115-wrapper.sh",
            )
            executable_files = {
                "emby-115-guard",
                "ensure-emby-115-guard.sh",
                "ensure-emby-115-proxy.sh",
                "install-emby-115-runtime.sh",
                "install-emby-115-proxy-after-start.sh",
                "ensure-emby-websocket-timeout.sh",
                "ensure-emby-web-cache-buster.sh",
                "ensure-emby-docker-upstream.sh",
                "ensure-emby-direct-link-fallback.sh",
                "ensure-emby-openlist-resolver.sh",
                "ensure-emby-manifest-backend.sh",
                "ensure-emby-nginx-rlimit.sh",
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
                "    echo 'keys_zone=emby_115_slices:16m'\n"
                "    echo 'proxy_cache emby_115_slices;'\n"
                "    echo 'slice 1m;'\n"
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
                    "EMBY_NJS_SCRIPT": str(emby_js),
                    "EMBY_115_RUNTIME_CONFIG": str(runtime_config),
                    "EMBY_115_SLICE_CACHE_DIR": str(root / "slice-cache"),
                    "EMBY_115_SLICE_CACHE_OWNER": f"{os.getuid()}:{os.getgid()}",
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
            fake_upstream_ensure = root / "ensure-upstream"
            fake_installer = root / "install-proxy"

            fake_guard_ensure.write_text("#!/bin/sh\nexit 0\n", encoding="utf-8")
            fake_guard_ensure.chmod(0o755)
            fake_upstream_ensure.write_text("#!/bin/sh\nexit 0\n", encoding="utf-8")
            fake_upstream_ensure.chmod(0o755)
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
                "  echo 'keys_zone=emby_115_slices:16m'\n"
                "  echo 'proxy_cache emby_115_slices;'\n"
                "  echo 'slice 1m;'\n"
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
                    "EMBY_UPSTREAM_ENSURE": str(fake_upstream_ensure),
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
        singleflight = (
            REPO_ROOT / "test" / "xiaoyaSliceSingleFlight.sh"
        ).read_text()

        self.assertEqual(2, integration.count("--limit-rate 128k"))
        self.assertIn("third range did not return 206", integration)
        self.assertIn(
            "a healthy downstream stream was terminated to make room for the seek",
            integration,
        )
        self.assertIn("third seek exceeded", integration)
        self.assertIn("connection limit breach counter increased", integration)
        self.assertIn("replacement counter increased", integration)
        self.assertIn("slot timeout counter increased", integration)
        self.assertIn("X-Emby-115-Proxy: dynamic", integration)
        self.assertIn("X-Emby-115-Guard: active", integration)
        self.assertIn("X-Emby-115-Slice-Cache:", integration)
        self.assertIn("sleep 2", singleflight)
        self.assertIn('first_cache" != "MISS', singleflight)
        self.assertIn('second_cache" != "HIT', singleflight)
        self.assertIn('upstream_requests" -ne 1', singleflight)
        self.assertIn("duplicate same-slice upstream requests", singleflight)

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


class OpenListDirectLinkResolutionTests(unittest.TestCase):
    """The guard owns Xiaoya's `/d/`, so njs must resolve against OpenList.

    Resolving through the guarded location returns the media body instead of
    the signed 302.  njs caps `ngx.fetch` at `max_response_body_size`, so a
    multi-gigabyte body raises an exception, `fetchXYApi` answers
    `error: xy_api fetch failed`, and every direct-play client receives
    HTTP 500.
    """

    def _run(self, script, nginx):
        return subprocess.run(
            [str(OPENLIST_RESOLVER)],
            capture_output=True,
            text=True,
            env=_resolver_env(script, nginx),
        )

    def test_direct_link_resolution_never_addresses_the_guarded_location(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            script = root / "emby.js"
            script.write_text(RESOLUTION_FIXTURE)
            nginx = _write_fake_nginx(root / "nginx")

            first = self._run(script, nginx)
            patched = script.read_text()
            second = self._run(script, nginx)

            self.assertEqual(0, first.returncode, first.stderr)
            self.assertEqual("patched", first.stdout.strip())
            self.assertEqual(0, second.returncode, second.stderr)
            self.assertEqual("already-present", second.stdout.strip())
            self.assertEqual(patched, script.read_text())

            for line in patched.splitlines():
                if "alistFilePath =" in line or "alistNextPath =" in line:
                    self.assertNotIn("'%s'" % GUARDED_ORIGIN, line)
                    self.assertIn("'%s'" % OPENLIST_ORIGIN, line)
            self.assertEqual(
                2, patched.count("codex-emby-openlist-resolver-v1")
            )
            self.assertEqual(
                6, patched.count("'%s'" % OPENLIST_ORIGIN)
            )

    def test_rewrite_converges_after_an_interrupted_run(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            nginx = _write_fake_nginx(root / "nginx")

            complete = root / "complete.js"
            complete.write_text(RESOLUTION_FIXTURE)
            self.assertEqual(0, self._run(complete, nginx).returncode)

            partial = root / "partial.js"
            partial.write_text(
                RESOLUTION_FIXTURE.replace(
                    "    var alistFilePath =",
                    "    // codex-emby-openlist-resolver-v1\n"
                    "    var alistFilePath =",
                    1,
                )
            )
            recovered = self._run(partial, nginx)

            self.assertEqual(0, recovered.returncode, recovered.stderr)
            self.assertEqual(complete.read_text(), partial.read_text())

    def test_restores_the_script_when_nginx_rejects_the_rewrite(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            script = root / "emby.js"
            script.write_text(RESOLUTION_FIXTURE)
            nginx = _write_fake_nginx(root / "nginx", exit_code=1)

            rejected = self._run(script, nginx)

            self.assertNotEqual(0, rejected.returncode)
            self.assertEqual(RESOLUTION_FIXTURE, script.read_text())

    def test_missing_resolution_fails_without_touching_the_script(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            script = root / "emby.js"
            script.write_text("var unrelated = 1;\n")
            nginx = _write_fake_nginx(root / "nginx")

            missing = self._run(script, nginx)

            self.assertNotEqual(0, missing.returncode)
            self.assertIn("no OpenList direct-link resolution", missing.stderr)
            self.assertEqual("var unrelated = 1;\n", script.read_text())

    def test_resolved_script_requests_the_openlist_listener(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            script = root / "emby.js"
            script.write_text(RESOLUTION_FIXTURE)
            nginx = _write_fake_nginx(root / "nginx")
            self.assertEqual(0, self._run(script, nginx).returncode)

            observed = subprocess.run(
                [
                    "node",
                    "-e",
                    "var nextPath='http://xiaoya.host:5678/d/show/next.mp4';"
                    "var embyRes='http://xiaoya.host:5678/d/show/current.mp4';"
                    + script.read_text()
                    + "redirect2Pan({}).then(function(resolved){"
                    "process.stdout.write(JSON.stringify(resolved))})",
                ],
                capture_output=True,
                text=True,
            )

            self.assertEqual(0, observed.returncode, observed.stderr)
            self.assertEqual(
                '["http://127.0.0.1:5244/d/show/current.mp4?sign=",'
                '"http://127.0.0.1:5244/d/show/next.mp4?sign="]',
                observed.stdout,
            )


class ServerRenderedManifestRoutingTests(unittest.TestCase):
    """A manifest request asks the server to produce a stream.

    Xiaoya routes `/videos/*/master` and `/videos/*/live` through njs next to
    `/videos/*/original`, and answers all of them with a direct-link 302.  A
    redirect to the original file on the provider's CDN cannot satisfy an
    `.m3u8` request, so Emby Web's player fails and retries whenever it has to
    transcode.
    """

    FIXTURE = """async function redirect2Pan(r) {
    var api_key = r.args.api_key || 'fixture-token';

    if (r.uri.indexOf("Subtitles") !== -1) {
        r.internalRedirect("@backend");
        return;
    }

    r.return(302, 'https://cdn.invalid/original.mp4');
}
"""

    def _run(self, script, nginx):
        return subprocess.run(
            [str(MANIFEST_ROUTER)],
            capture_output=True,
            text=True,
            env=_resolver_env(script, nginx),
        )

    def test_manifest_requests_stay_on_the_emby_backend(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            script = root / "emby.js"
            script.write_text(self.FIXTURE)
            nginx = _write_fake_nginx(root / "nginx")

            first = self._run(script, nginx)
            patched = script.read_text()
            second = self._run(script, nginx)

            self.assertEqual(0, first.returncode, first.stderr)
            self.assertEqual("patched", first.stdout.strip())
            self.assertEqual("already-present", second.stdout.strip())
            self.assertEqual(patched, script.read_text())

            observed = subprocess.run(
                [
                    "node",
                    "-e",
                    patched
                    + "function drive(uri){var actions=[];var r={uri:uri,"
                    "args:{api_key:'token'},"
                    "internalRedirect:function(v){actions.push(v)},"
                    "return:function(status){actions.push(status)}};"
                    "return redirect2Pan(r).then(function(){return actions})}"
                    "Promise.all(["
                    "drive('/emby/videos/1/master.m3u8'),"
                    "drive('/emby/videos/1/live.m3u8'),"
                    "drive('/emby/videos/1/original.mp4'),"
                    "drive('/emby/videos/1/stream.mp4')"
                    "]).then(function(v){process.stdout.write(JSON.stringify(v))})",
                ],
                capture_output=True,
                text=True,
            )

            self.assertEqual(0, observed.returncode, observed.stderr)
            self.assertEqual(
                '[["@backend"],["@backend"],[302],[302]]', observed.stdout
            )

    def test_incomplete_patch_is_reported_instead_of_reapplied(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            script = root / "emby.js"
            script.write_text(
                "    // codex-emby-manifest-backend-v1\n" + self.FIXTURE
            )
            nginx = _write_fake_nginx(root / "nginx")

            result = self._run(script, nginx)

            self.assertNotEqual(0, result.returncode)
            self.assertIn("incomplete", result.stderr)

    def test_missing_anchor_fails_without_touching_the_script(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            script = root / "emby.js"
            script.write_text("var unrelated = 1;\n")
            nginx = _write_fake_nginx(root / "nginx")

            result = self._run(script, nginx)

            self.assertNotEqual(0, result.returncode)
            self.assertEqual("var unrelated = 1;\n", script.read_text())


class NginxWorkerDescriptorLimitTests(unittest.TestCase):
    """A 1 MiB slice cache exhausts the stock 1024 descriptor soft limit.

    An exhausted worker logs `(24: No file descriptors available)` and
    truncates the stream mid-playback.
    """

    MAIN_CONFIG = (
        "user nginx;\n"
        "worker_processes auto;\n"
        "include /etc/nginx/modules/*.conf;\n"
        "include /etc/nginx/conf.d/*.conf;\n"
        "\n"
        "events {\n"
        "\tworker_connections 1024;\n"
        "}\n"
    )

    def _run(self, root, main_config, nginx):
        return subprocess.run(
            [str(RLIMIT_INSTALLER)],
            capture_output=True,
            text=True,
            env={
                "PATH": "/usr/bin:/bin",
                "EMBY_RLIMIT_SOURCE": str(RLIMIT_CONFIG),
                "EMBY_RLIMIT_TARGET_DIR": str(root / "conf.d"),
                "EMBY_RLIMIT_TARGET": str(
                    root / "conf.d" / "emby-nginx-rlimit.conf"
                ),
                "EMBY_NGINX_MAIN_CONFIG": str(main_config),
                "NGINX_BIN": str(nginx),
            },
        )

    def test_limit_is_installed_as_a_main_context_include(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            main_config = root / "nginx.conf"
            main_config.write_text(self.MAIN_CONFIG)
            nginx = _write_fake_nginx(root / "nginx")

            first = self._run(root, main_config, nginx)
            second = self._run(root, main_config, nginx)
            installed = (root / "conf.d" / "emby-nginx-rlimit.conf").read_text()

            self.assertEqual(0, first.returncode, first.stderr)
            self.assertEqual("patched", first.stdout.strip())
            self.assertEqual("already-present", second.stdout.strip())
            self.assertIn("worker_rlimit_nofile 65535;", installed)
            self.assertEqual(RLIMIT_CONFIG.read_text(), installed)

    def test_limit_is_refused_when_the_include_is_not_main_context(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            main_config = root / "nginx.conf"
            main_config.write_text(
                self.MAIN_CONFIG.replace(
                    "include /etc/nginx/conf.d/*.conf;\n", ""
                )
            )
            nginx = _write_fake_nginx(root / "nginx")

            refused = self._run(root, main_config, nginx)

            self.assertNotEqual(0, refused.returncode)
            self.assertIn("main context", refused.stderr)
            self.assertFalse((root / "conf.d").exists())

    def test_limit_is_removed_when_nginx_rejects_it(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            main_config = root / "nginx.conf"
            main_config.write_text(self.MAIN_CONFIG)
            nginx = _write_fake_nginx(root / "nginx", exit_code=1)

            rejected = self._run(root, main_config, nginx)

            self.assertNotEqual(0, rejected.returncode)
            self.assertFalse(
                (root / "conf.d" / "emby-nginx-rlimit.conf").exists()
            )


class RepairStepDeploymentTests(unittest.TestCase):
    def test_both_installers_deploy_and_run_the_new_repair_steps(self):
        steps = (
            "ensure-emby-openlist-resolver.sh",
            "ensure-emby-manifest-backend.sh",
            "ensure-emby-nginx-rlimit.sh",
        )
        for installer_name in (
            "install-emby-115-proxy.sh",
            "install-emby-115-runtime.sh",
        ):
            installer = (DEPLOY_ROOT / installer_name).read_text()
            self.assertIn("emby-nginx-rlimit.conf", installer, installer_name)
            for step in steps:
                self.assertIn(step, installer, installer_name)
                self.assertIn(
                    '[ ! -x "$data_dir/%s" ]' % step, installer, installer_name
                )
                self.assertIn(
                    '"$data_dir/%s"\n' % step, installer, installer_name
                )

    def test_the_routing_patch_keeps_every_xiaoya_host_rewrite(self):
        # A media path reported as `http://xiaoya.host:5678/d/...` must be
        # rewritten onto the resolution origin; dropping the rewrite sends the
        # resolution back through the guarded location.
        patcher = (DEPLOY_ROOT / "ensure-emby-direct-link-fallback.sh").read_text()
        emitted = [
            line
            for line in patcher.splitlines()
            if "var alistFilePath = embyRes.replace(" in line
        ]
        self.assertEqual(1, len(emitted))
        for host in ("DOCKER_ADDRESS", "172.19.0.1:5678", "xiaoya.host:5678"):
            self.assertIn(host, emitted[0])


if __name__ == "__main__":
    unittest.main()
