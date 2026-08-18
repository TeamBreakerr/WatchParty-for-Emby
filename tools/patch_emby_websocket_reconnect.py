#!/usr/bin/env python3
"""Patch Emby Web's foreground lifecycle to rebuild a stale WebSocket."""

import argparse
import os
import stat
import sys
import tempfile
from pathlib import Path


OPEN_WEBSOCKET_ORIGINAL = (
    '{key:"openWebSocket",value:function(){var accessToken=this.accessToken();'
)
OPEN_WEBSOCKET_PATCHED = (
    '{key:"openWebSocket",value:function(){'
    'this._webSocketAutoReconnectSuppressed=!1;var accessToken=this.accessToken();'
)
ON_OPEN_ORIGINAL = (
    'serverUrl.onopen=function(){console.log("web socket connection opened"),'
)
ON_OPEN_PATCHED = (
    'serverUrl.onopen=function(){this._webSocketReconnectDelay=1e3,'
    'clearTimeout(this._webSocketReconnectTimer),this._webSocketReconnectTimer=null,'
    'console.log("web socket connection opened"),'
)
ON_CLOSE_ORIGINAL = (
    '(socket=serverUrl).onclose=function(){console.log("web socket closed"),'
    'apiClient._webSocket===socket&&(console.log("nulling out web socket"),'
    'apiClient._webSocket=null),setTimeout(function(){'
    '_events.default.trigger(apiClient,"websocketclose")},0)}'
)
ON_CLOSE_PATCHED_V1 = (
    '(socket=serverUrl).onclose=function(){var delay;console.log("web socket closed"),'
    'apiClient._webSocket===socket&&(console.log("nulling out web socket"),'
    'apiClient._webSocket=null),setTimeout(function(){'
    '_events.default.trigger(apiClient,"websocketclose"),'
    'apiClient._webSocketAutoReconnectSuppressed||!apiClient.enableWebSocketAutoConnect||'
    '(delay=Math.min(apiClient._webSocketReconnectDelay||1e3,3e4),'
    'apiClient._webSocketReconnectDelay=Math.min(2*delay,3e4),'
    'clearTimeout(apiClient._webSocketReconnectTimer),'
    'apiClient._webSocketReconnectTimer=setTimeout(function(){'
    'apiClient._webSocketReconnectTimer=null,apiClient.ensureWebSocket()},delay))},0)}'
)
ON_CLOSE_PATCHED = (
    '(socket=serverUrl).onclose=function(){var delay,reconnect=apiClient._webSocket===socket;'
    'console.log("web socket closed"),'
    'reconnect&&(console.log("nulling out web socket"),'
    'apiClient._webSocket=null),setTimeout(function(){'
    '_events.default.trigger(apiClient,"websocketclose"),'
    'reconnect&&!apiClient._webSocketAutoReconnectSuppressed&&'
    'apiClient.enableWebSocketAutoConnect&&'
    '(delay=Math.min(apiClient._webSocketReconnectDelay||1e3,3e4),'
    'apiClient._webSocketReconnectDelay=Math.min(2*delay,3e4),'
    'clearTimeout(apiClient._webSocketReconnectTimer),'
    'apiClient._webSocketReconnectTimer=setTimeout(function(){'
    'apiClient._webSocketReconnectTimer=null,apiClient.ensureWebSocket()},delay))},0)}'
)
CLOSE_WEBSOCKET_ORIGINAL = (
    '{key:"closeWebSocket",value:function(){var socket=this._webSocket;'
    'socket&&socket.readyState===WebSocket.OPEN&&socket.close()}}'
)
CLOSE_WEBSOCKET_PATCHED_V1 = (
    '{key:"closeWebSocket",value:function(){'
    'this._webSocketAutoReconnectSuppressed=!0,'
    'clearTimeout(this._webSocketReconnectTimer),this._webSocketReconnectTimer=null;'
    'var socket=this._webSocket;'
    'socket&&socket.readyState===WebSocket.OPEN&&socket.close()}}'
)
CLOSE_WEBSOCKET_PATCHED = (
    '{key:"closeWebSocket",value:function(){'
    'this._webSocketAutoReconnectSuppressed=!0,'
    'clearTimeout(this._webSocketReconnectTimer),this._webSocketReconnectTimer=null,'
    'clearTimeout(this._webSocketRestartTimer),this._webSocketRestartTimer=null;'
    'var socket=this._webSocket;this._webSocket=null;'
    'try{socket&&socket.close()}catch(err){}}}'
)
RESTART_WEBSOCKET_ORIGINAL = '{key:"sendWebSocketMessage"'
RESTART_WEBSOCKET_PATCHED_V1 = (
    '{key:"restartWebSocket",value:function(){var socket=this._webSocket;'
    'this._webSocket=null,clearTimeout(this._webSocketRestartTimer),'
    'this._webSocketRestartTimer=null;try{socket&&socket.close()}catch(err){}'
    'this.enableWebSocketAutoConnect&&(this._webSocketRestartTimer=setTimeout('
    'function(){this._webSocketRestartTimer=null,this.ensureWebSocket()}.bind(this),300))}},'
    '{key:"sendWebSocketMessage"'
)
RESTART_WEBSOCKET_PATCHED = (
    '{key:"restartWebSocket",value:function(){var socket=this._webSocket;'
    'this._webSocket=null,clearTimeout(this._webSocketReconnectTimer),'
    'this._webSocketReconnectTimer=null,clearTimeout(this._webSocketRestartTimer),'
    'this._webSocketRestartTimer=null;try{socket&&socket.close()}catch(err){}'
    'this.enableWebSocketAutoConnect&&(this._webSocketRestartTimer=setTimeout('
    'function(){this._webSocketRestartTimer=null,this.ensureWebSocket()}.bind(this),300))}},'
    '{key:"sendWebSocketMessage"'
)
CONNECTION_MANAGER_ORIGINAL = (
    '{key:"onAppResume",value:function(){for(var apiClients=this._apiClients,'
    'i=0,length=apiClients.length;i<length;i++)apiClients[i].ensureWebSocket()}}'
)
CONNECTION_MANAGER_PATCHED = (
    '{key:"onAppResume",value:function(){for(var apiClients=this._apiClients,'
    'i=0,length=apiClients.length;i<length;i++)apiClients[i].restartWebSocket()}}'
)


def patch_text(
    text: str,
    original: str,
    patched: str,
    source: Path,
    legacy_patched=(),
):
    recognized_patched = (patched, *legacy_patched)
    patched_counts = [text.count(candidate) for candidate in recognized_patched]
    text_without_patched = text
    for candidate in recognized_patched:
        text_without_patched = text_without_patched.replace(candidate, "")
    original_count = text_without_patched.count(original)
    state_count = original_count + sum(patched_counts)
    if state_count != 1:
        raise RuntimeError(f"expected exactly one original or patched point in {source}")
    if patched_counts[0] == 1:
        return text, False
    if original_count == 1:
        return text.replace(original, patched, 1), True
    for candidate, count in zip(legacy_patched, patched_counts[1:]):
        if count == 1:
            return text.replace(candidate, patched, 1), True
    raise RuntimeError(f"expected exactly one original or patched point in {source}")


def replace_files_transactionally(replacements):
    pending = []
    originals = {}
    replaced = []
    try:
        for path, contents in replacements:
            originals[path] = path.read_bytes()
            mode = stat.S_IMODE(path.stat().st_mode)
            with tempfile.NamedTemporaryFile(
                mode="w",
                encoding="utf-8",
                dir=path.parent,
                prefix=f".{path.name}.codex-",
                delete=False,
            ) as temporary:
                temporary.write(contents)
                temporary_path = Path(temporary.name)
            os.chmod(temporary_path, mode)
            pending.append((path, temporary_path))
        for path, temporary_path in pending:
            os.replace(temporary_path, path)
            replaced.append(path)
    except OSError:
        for path in reversed(replaced):
            path.write_bytes(originals[path])
        raise
    finally:
        for _, temporary_path in pending:
            temporary_path.unlink(missing_ok=True)


def patch_dashboard(dashboard_root: Path) -> bool:
    module_root = dashboard_root / "modules" / "emby-apiclient"
    api_client = module_root / "apiclient.js"
    connection_manager = module_root / "connectionmanager.js"

    api_text = api_client.read_text()
    connection_text = connection_manager.read_text()
    api_changed = False
    for original, patched, legacy_patched in (
        (OPEN_WEBSOCKET_ORIGINAL, OPEN_WEBSOCKET_PATCHED, ()),
        (ON_OPEN_ORIGINAL, ON_OPEN_PATCHED, ()),
        (ON_CLOSE_ORIGINAL, ON_CLOSE_PATCHED, (ON_CLOSE_PATCHED_V1,)),
        (
            CLOSE_WEBSOCKET_ORIGINAL,
            CLOSE_WEBSOCKET_PATCHED,
            (CLOSE_WEBSOCKET_PATCHED_V1,),
        ),
        (
            RESTART_WEBSOCKET_ORIGINAL,
            RESTART_WEBSOCKET_PATCHED,
            (RESTART_WEBSOCKET_PATCHED_V1,),
        ),
    ):
        api_text, changed = patch_text(
            api_text,
            original,
            patched,
            api_client,
            legacy_patched,
        )
        api_changed = api_changed or changed
    connection_text, connection_changed = patch_text(
        connection_text,
        CONNECTION_MANAGER_ORIGINAL,
        CONNECTION_MANAGER_PATCHED,
        connection_manager,
    )
    replacements = []
    if api_changed:
        replacements.append((api_client, api_text))
    if connection_changed:
        replacements.append((connection_manager, connection_text))
    replace_files_transactionally(replacements)
    return api_changed or connection_changed


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dashboard-root", required=True, type=Path)
    args = parser.parse_args()
    try:
        changed = patch_dashboard(args.dashboard_root)
    except (OSError, RuntimeError) as error:
        print(f"incompatible Emby dashboard: {error}", file=sys.stderr)
        return 3
    print("patched" if changed else "already-patched")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
