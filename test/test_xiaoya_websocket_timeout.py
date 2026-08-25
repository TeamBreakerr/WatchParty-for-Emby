import os
import re
import subprocess
import tempfile
import unittest
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[1]
DEPLOY_ROOT = REPO_ROOT / "deploy" / "xiaoya-115-proxy"
PATCHER = DEPLOY_ROOT / "ensure-emby-websocket-timeout.sh"
INCLUDE = DEPLOY_ROOT / "emby-websocket-timeout.conf"
DIAGNOSTIC = DEPLOY_ROOT / "emby-websocket-diagnostic.conf"
NPM_LOCATION = REPO_ROOT / ".codex-npm-emby-websocket.conf"


CONFIG_FIXTURE = """server {
    proxy_read_timeout 20s;
    location ~ /(socket|embywebsocket) {
        proxy_pass http://backend;
    }
}

server {
    proxy_read_timeout 20s;
    location ~ /(socket|embywebsocket) {
        proxy_pass http://backend;
    }
}
"""


class XiaoyaWebSocketTimeoutTests(unittest.TestCase):
    def _run_patcher(
        self,
        config,
        include,
        diagnostic_source,
        diagnostic_runtime,
        fake_nginx,
    ):
        environment = os.environ.copy()
        environment.update(
            {
                "EMBY_NGINX_CONFIG": str(config),
                "EMBY_WEBSOCKET_INCLUDE": str(include),
                "EMBY_WEBSOCKET_DIAGNOSTIC_SOURCE": str(diagnostic_source),
                "EMBY_WEBSOCKET_DIAGNOSTIC_RUNTIME": str(diagnostic_runtime),
                "NGINX_BIN": str(fake_nginx),
            }
        )
        return subprocess.run(
            [str(PATCHER)],
            check=False,
            capture_output=True,
            text=True,
            env=environment,
        )

    def test_patches_every_websocket_location_and_is_idempotent(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            config = root / "emby.conf"
            include = root / "websocket-timeout.conf"
            diagnostic_source = root / "websocket-diagnostic-source.conf"
            diagnostic_runtime = root / "websocket-diagnostic-runtime.conf"
            fake_nginx = root / "nginx"
            config.write_text(CONFIG_FIXTURE, encoding="utf-8")
            include.write_text("proxy_read_timeout 3600s;\n", encoding="utf-8")
            diagnostic_source.write_text(
                "log_format emby_websocket_diagnostic '$status';\n",
                encoding="utf-8",
            )
            fake_nginx.write_text("#!/bin/sh\nexit 0\n", encoding="utf-8")
            fake_nginx.chmod(0o755)

            first = self._run_patcher(
                config,
                include,
                diagnostic_source,
                diagnostic_runtime,
                fake_nginx,
            )
            first_contents = config.read_bytes()
            second = self._run_patcher(
                config,
                include,
                diagnostic_source,
                diagnostic_runtime,
                fake_nginx,
            )

            self.assertEqual(0, first.returncode, first.stderr)
            self.assertEqual(0, second.returncode, second.stderr)
            self.assertEqual(2, config.read_text(encoding="utf-8").count(str(include)))
            self.assertEqual(first_contents, config.read_bytes())
            self.assertEqual(diagnostic_source.read_bytes(), diagnostic_runtime.read_bytes())

    def test_restores_the_original_config_when_nginx_rejects_the_patch(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            config = root / "emby.conf"
            include = root / "websocket-timeout.conf"
            diagnostic_source = root / "websocket-diagnostic-source.conf"
            diagnostic_runtime = root / "websocket-diagnostic-runtime.conf"
            fake_nginx = root / "nginx"
            config.write_text(CONFIG_FIXTURE, encoding="utf-8")
            original = config.read_bytes()
            include.write_text("proxy_read_timeout 3600s;\n", encoding="utf-8")
            diagnostic_source.write_text(
                "log_format emby_websocket_diagnostic '$status';\n",
                encoding="utf-8",
            )
            fake_nginx.write_text("#!/bin/sh\nexit 1\n", encoding="utf-8")
            fake_nginx.chmod(0o755)

            result = self._run_patcher(
                config,
                include,
                diagnostic_source,
                diagnostic_runtime,
                fake_nginx,
            )

            self.assertNotEqual(0, result.returncode)
            self.assertEqual(original, config.read_bytes())
            self.assertFalse(diagnostic_runtime.exists())

    def test_websocket_timeout_is_twenty_four_hours_at_both_proxy_layers(self):
        inner = INCLUDE.read_text(encoding="utf-8")
        outer = NPM_LOCATION.read_text(encoding="utf-8")

        for configuration in (inner, outer):
            self.assertIn("proxy_read_timeout 86400s;", configuration)
            self.assertIn("proxy_send_timeout 86400s;", configuration)
            self.assertNotIn("proxy_read_timeout 3600s;", configuration)
            self.assertNotIn("proxy_send_timeout 3600s;", configuration)

    def test_diagnostic_log_records_transport_evidence_without_identifiers(self):
        diagnostic = DIAGNOSTIC.read_text(encoding="utf-8")
        inner = INCLUDE.read_text(encoding="utf-8")
        outer = NPM_LOCATION.read_text(encoding="utf-8")

        self.assertIn("log_format emby_websocket_diagnostic", diagnostic)
        self.assertIn("$emby_websocket_layer", diagnostic)
        self.assertIn("$emby_websocket_termination_hint", diagnostic)
        self.assertIn("$connection", diagnostic)
        self.assertIn("$status", diagnostic)
        self.assertIn("$request_time", diagnostic)
        self.assertIn("$request_completion", diagnostic)
        self.assertIn("$upstream_status", diagnostic)
        self.assertIn("downstream_or_network", diagnostic)
        self.assertIn("emby_upstream", diagnostic)
        self.assertIn("nginx_idle_timeout", diagnostic)
        self.assertIn('"configured_idle_timeout_seconds":"86400"', diagnostic)

        self.assertIn("emby-websocket-diagnostic.log", inner)
        self.assertIn("emby-websocket-diagnostic.log", outer)
        self.assertIn("map $server_port $emby_websocket_layer", diagnostic)

        privacy_sensitive_variables = (
            "$remote_addr",
            "$remote_user",
            "$request",
            "$request_uri",
            "$uri",
            "$args",
            "$query_string",
            "$http_authorization",
            "$http_cookie",
            "$http_user_agent",
            "$http_x_emby_token",
            "$arg_api_key",
        )
        combined = diagnostic + inner + outer
        for variable in privacy_sensitive_variables:
            self.assertIsNone(
                re.search(re.escape(variable) + r"(?![A-Za-z0-9_])", combined),
                variable,
            )


if __name__ == "__main__":
    unittest.main()
