import os
import subprocess
import tempfile
import unittest
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[1]
DEPLOY_ROOT = REPO_ROOT / "deploy" / "xiaoya-115-proxy"
PATCHER = DEPLOY_ROOT / "ensure-emby-websocket-timeout.sh"
INCLUDE = DEPLOY_ROOT / "emby-websocket-timeout.conf"


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
    def _run_patcher(self, config, include, fake_nginx):
        environment = os.environ.copy()
        environment.update(
            {
                "EMBY_NGINX_CONFIG": str(config),
                "EMBY_WEBSOCKET_INCLUDE": str(include),
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
            fake_nginx = root / "nginx"
            config.write_text(CONFIG_FIXTURE, encoding="utf-8")
            include.write_text("proxy_read_timeout 3600s;\n", encoding="utf-8")
            fake_nginx.write_text("#!/bin/sh\nexit 0\n", encoding="utf-8")
            fake_nginx.chmod(0o755)

            first = self._run_patcher(config, include, fake_nginx)
            first_contents = config.read_bytes()
            second = self._run_patcher(config, include, fake_nginx)

            self.assertEqual(0, first.returncode, first.stderr)
            self.assertEqual(0, second.returncode, second.stderr)
            self.assertEqual(2, config.read_text(encoding="utf-8").count(str(include)))
            self.assertEqual(first_contents, config.read_bytes())

    def test_restores_the_original_config_when_nginx_rejects_the_patch(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            config = root / "emby.conf"
            include = root / "websocket-timeout.conf"
            fake_nginx = root / "nginx"
            config.write_text(CONFIG_FIXTURE, encoding="utf-8")
            original = config.read_bytes()
            include.write_text("proxy_read_timeout 3600s;\n", encoding="utf-8")
            fake_nginx.write_text("#!/bin/sh\nexit 1\n", encoding="utf-8")
            fake_nginx.chmod(0o755)

            result = self._run_patcher(config, include, fake_nginx)

            self.assertNotEqual(0, result.returncode)
            self.assertEqual(original, config.read_bytes())


if __name__ == "__main__":
    unittest.main()
