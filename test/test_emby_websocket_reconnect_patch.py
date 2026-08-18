import importlib.util
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock


REPO_ROOT = Path(__file__).resolve().parents[1]
PATCHER = REPO_ROOT / "tools" / "patch_emby_websocket_reconnect.py"
PATCHER_SPEC = importlib.util.spec_from_file_location("emby_ws_patcher", PATCHER)
PATCHER_MODULE = importlib.util.module_from_spec(PATCHER_SPEC)
PATCHER_SPEC.loader.exec_module(PATCHER_MODULE)

API_CLIENT_ORIGINAL = (
    '{key:"openWebSocket",value:function(){var accessToken=this.accessToken();'
    'serverUrl.onopen=function(){console.log("web socket connection opened"),'
    '_events.default.trigger(this,"websocketopen")}.bind(this),apiClient=this,'
    '(socket=serverUrl).onclose=function(){console.log("web socket closed"),'
    'apiClient._webSocket===socket&&(console.log("nulling out web socket"),'
    'apiClient._webSocket=null),setTimeout(function(){'
    '_events.default.trigger(apiClient,"websocketclose")},0)},'
    'this._webSocket=serverUrl}},'
    '{key:"closeWebSocket",value:function(){var socket=this._webSocket;'
    'socket&&socket.readyState===WebSocket.OPEN&&socket.close()}},'
    '{key:"sendWebSocketMessage",value:function(name,data){return name}}'
)
CONNECTION_MANAGER_ORIGINAL = (
    '{key:"onAppResume",value:function(){for(var apiClients=this._apiClients,'
    'i=0,length=apiClients.length;i<length;i++)apiClients[i].ensureWebSocket()}}'
)


class EmbyWebSocketReconnectPatchTests(unittest.TestCase):
    def test_patch_forces_a_fresh_socket_when_the_app_resumes(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            dashboard_root = Path(temp_dir)
            api_client, connection_manager = self._write_dashboard_fixture(dashboard_root)

            result = subprocess.run(
                [sys.executable, str(PATCHER), "--dashboard-root", str(dashboard_root)],
                check=False,
                capture_output=True,
                text=True,
            )

            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn('key:"restartWebSocket"', api_client.read_text())
            self.assertIn('apiClients[i].restartWebSocket()', connection_manager.read_text())
            self.assertNotIn(
                'apiClients[i].ensureWebSocket()',
                connection_manager.read_text(),
            )

    def test_patch_reconnects_after_an_unexpected_close_with_bounded_backoff(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            dashboard_root = Path(temp_dir)
            api_client, _ = self._write_dashboard_fixture(dashboard_root)

            result = subprocess.run(
                [sys.executable, str(PATCHER), "--dashboard-root", str(dashboard_root)],
                check=False,
                capture_output=True,
                text=True,
            )

            patched = api_client.read_text()
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn("_webSocketAutoReconnectSuppressed=!0", patched)
            self.assertIn("_webSocketReconnectDelay=1e3", patched)
            self.assertIn("Math.min(2*delay,3e4)", patched)
            self.assertIn("apiClient.ensureWebSocket()", patched)
            self.assertIn("reconnect=apiClient._webSocket===socket", patched)

    def test_logout_cancels_every_reconnect_path_and_closes_a_connecting_socket(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            dashboard_root = Path(temp_dir)
            api_client, _ = self._write_dashboard_fixture(dashboard_root)

            result = subprocess.run(
                [sys.executable, str(PATCHER), "--dashboard-root", str(dashboard_root)],
                check=False,
                capture_output=True,
                text=True,
            )

            patched = api_client.read_text()
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn("clearTimeout(this._webSocketReconnectTimer)", patched)
            self.assertIn("clearTimeout(this._webSocketRestartTimer)", patched)
            self.assertIn("var socket=this._webSocket;this._webSocket=null", patched)
            self.assertIn("try{socket&&socket.close()}catch(err){}", patched)

    @unittest.skipUnless(shutil.which("node"), "Node.js is required")
    def test_runtime_timing_avoids_stale_socket_reconnects_and_honors_logout(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            dashboard_root = Path(temp_dir)
            api_client, _ = self._write_dashboard_fixture(dashboard_root)
            result = subprocess.run(
                [sys.executable, str(PATCHER), "--dashboard-root", str(dashboard_root)],
                check=False,
                capture_output=True,
                text=True,
            )
            self.assertEqual(0, result.returncode, result.stderr)
            source = api_client.read_text()
            fragment = source.removeprefix("define(function(){").removesuffix("});")
            runtime_test = f"""
const assert = require("node:assert/strict");
const console = {{ log() {{}} }};
const _events = {{ default: {{ trigger() {{}} }} }};
const methods = Object.fromEntries(
    [{fragment}].map(entry => [entry.key, entry.value])
);
let nextTimer = 1;
const timers = new Map();
global.setTimeout = (callback, delay) => {{
    const id = nextTimer++;
    timers.set(id, {{ callback, delay }});
    return id;
}};
global.clearTimeout = id => timers.delete(id);
function runTimer(delay) {{
    const entry = [...timers.entries()].find(([, timer]) => timer.delay === delay);
    assert.ok(entry, `missing timer with delay ${{delay}}`);
    timers.delete(entry[0]);
    entry[1].callback();
}}
function createSocket() {{
    return {{
        closeCalls: 0,
        close() {{
            this.closeCalls += 1;
            if (this.onclose) this.onclose();
        }}
    }};
}}
const client = {{
    enableWebSocketAutoConnect: true,
    accessToken() {{ return "token"; }},
    ensureCalls: 0,
    ensureWebSocket() {{ this.ensureCalls += 1; }}
}};

global.serverUrl = createSocket();
methods.openWebSocket.call(client);
const staleSocket = global.serverUrl;
methods.restartWebSocket.call(client);
assert.equal(staleSocket.closeCalls, 1);
runTimer(0);
assert.deepEqual([...timers.values()].map(timer => timer.delay), [300]);
methods.closeWebSocket.call(client);
assert.equal(timers.size, 0);
assert.equal(client.ensureCalls, 0);

global.serverUrl = createSocket();
methods.openWebSocket.call(client);
const activeSocket = global.serverUrl;
activeSocket.readyState = 0;
methods.closeWebSocket.call(client);
assert.equal(activeSocket.closeCalls, 1);
assert.equal(client._webSocket, null);
assert.deepEqual([...timers.values()].map(timer => timer.delay), [0]);
runTimer(0);
assert.equal(timers.size, 0);
assert.equal(client.ensureCalls, 0);

global.serverUrl = createSocket();
methods.openWebSocket.call(client);
global.serverUrl.onclose();
runTimer(0);
assert.deepEqual([...timers.values()].map(timer => timer.delay), [1000]);
runTimer(1000);
assert.equal(client.ensureCalls, 1);
"""
            runtime_result = subprocess.run(
                [shutil.which("node"), "-e", runtime_test],
                check=False,
                capture_output=True,
                text=True,
            )
            self.assertEqual(0, runtime_result.returncode, runtime_result.stderr)

    def test_patch_is_idempotent(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            dashboard_root = Path(temp_dir)
            api_client, connection_manager = self._write_dashboard_fixture(dashboard_root)
            command = [
                sys.executable,
                str(PATCHER),
                "--dashboard-root",
                str(dashboard_root),
            ]

            first = subprocess.run(command, check=False, capture_output=True, text=True)
            first_contents = (api_client.read_bytes(), connection_manager.read_bytes())
            second = subprocess.run(command, check=False, capture_output=True, text=True)

            self.assertEqual(0, first.returncode, first.stderr)
            self.assertEqual(0, second.returncode, second.stderr)
            self.assertIn("already-patched", second.stdout)
            self.assertEqual(
                first_contents,
                (api_client.read_bytes(), connection_manager.read_bytes()),
            )
            self.assertEqual(1, api_client.read_text().count('key:"restartWebSocket"'))

    def test_patch_upgrades_the_previous_reconnect_patch(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            dashboard_root = Path(temp_dir)
            api_client, connection_manager = self._write_dashboard_fixture(dashboard_root)
            legacy_api = api_client.read_text()
            for original, legacy in (
                (
                    PATCHER_MODULE.OPEN_WEBSOCKET_ORIGINAL,
                    PATCHER_MODULE.OPEN_WEBSOCKET_PATCHED,
                ),
                (
                    PATCHER_MODULE.ON_OPEN_ORIGINAL,
                    PATCHER_MODULE.ON_OPEN_PATCHED,
                ),
                (
                    PATCHER_MODULE.ON_CLOSE_ORIGINAL,
                    PATCHER_MODULE.ON_CLOSE_PATCHED_V1,
                ),
                (
                    PATCHER_MODULE.CLOSE_WEBSOCKET_ORIGINAL,
                    PATCHER_MODULE.CLOSE_WEBSOCKET_PATCHED_V1,
                ),
                (
                    PATCHER_MODULE.RESTART_WEBSOCKET_ORIGINAL,
                    PATCHER_MODULE.RESTART_WEBSOCKET_PATCHED_V1,
                ),
            ):
                legacy_api = legacy_api.replace(original, legacy, 1)
            api_client.write_text(legacy_api)
            connection_manager.write_text(
                connection_manager.read_text().replace(
                    PATCHER_MODULE.CONNECTION_MANAGER_ORIGINAL,
                    PATCHER_MODULE.CONNECTION_MANAGER_PATCHED,
                    1,
                )
            )

            result = subprocess.run(
                [sys.executable, str(PATCHER), "--dashboard-root", str(dashboard_root)],
                check=False,
                capture_output=True,
                text=True,
            )

            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn("patched", result.stdout)
            self.assertIn(PATCHER_MODULE.ON_CLOSE_PATCHED, api_client.read_text())
            self.assertIn(PATCHER_MODULE.CLOSE_WEBSOCKET_PATCHED, api_client.read_text())
            self.assertIn(PATCHER_MODULE.RESTART_WEBSOCKET_PATCHED, api_client.read_text())

    def test_patch_refuses_an_unknown_upstream_without_partial_writes(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            dashboard_root = Path(temp_dir)
            api_client, connection_manager = self._write_dashboard_fixture(dashboard_root)
            connection_manager.write_text("define(function(){upstreamChanged:true});")
            before = (api_client.read_bytes(), connection_manager.read_bytes())

            result = subprocess.run(
                [sys.executable, str(PATCHER), "--dashboard-root", str(dashboard_root)],
                check=False,
                capture_output=True,
                text=True,
            )

            self.assertEqual(3, result.returncode)
            self.assertIn("incompatible", result.stderr)
            self.assertEqual(
                before,
                (api_client.read_bytes(), connection_manager.read_bytes()),
            )

    def test_patch_refuses_mixed_original_and_patched_state(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            dashboard_root = Path(temp_dir)
            api_client, connection_manager = self._write_dashboard_fixture(dashboard_root)
            connection_manager.write_text(
                connection_manager.read_text()
                + CONNECTION_MANAGER_ORIGINAL.replace(
                    "ensureWebSocket()",
                    "restartWebSocket()",
                )
            )
            before = (api_client.read_bytes(), connection_manager.read_bytes())

            result = subprocess.run(
                [sys.executable, str(PATCHER), "--dashboard-root", str(dashboard_root)],
                check=False,
                capture_output=True,
                text=True,
            )

            self.assertEqual(3, result.returncode)
            self.assertIn("incompatible", result.stderr)
            self.assertEqual(
                before,
                (api_client.read_bytes(), connection_manager.read_bytes()),
            )

    def test_second_file_replace_failure_restores_the_first_file(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            dashboard_root = Path(temp_dir)
            api_client, connection_manager = self._write_dashboard_fixture(dashboard_root)
            before = (api_client.read_bytes(), connection_manager.read_bytes())
            real_replace = os.replace
            replace_count = 0

            def fail_second_replace(source, destination):
                nonlocal replace_count
                replace_count += 1
                if replace_count == 2:
                    raise OSError("simulated second replace failure")
                real_replace(source, destination)

            with mock.patch.object(
                PATCHER_MODULE.os,
                "replace",
                side_effect=fail_second_replace,
            ):
                with self.assertRaises(OSError):
                    PATCHER_MODULE.patch_dashboard(dashboard_root)

            self.assertEqual(
                before,
                (api_client.read_bytes(), connection_manager.read_bytes()),
            )

    @staticmethod
    def _write_dashboard_fixture(dashboard_root: Path):
        module_root = dashboard_root / "modules" / "emby-apiclient"
        module_root.mkdir(parents=True)
        api_client = module_root / "apiclient.js"
        connection_manager = module_root / "connectionmanager.js"
        api_client.write_text(f"define(function(){{{API_CLIENT_ORIGINAL}}});")
        connection_manager.write_text(
            f"define(function(){{{CONNECTION_MANAGER_ORIGINAL}}});"
        )
        return api_client, connection_manager


if __name__ == "__main__":
    unittest.main()
