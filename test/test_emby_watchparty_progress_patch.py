import importlib.util
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[1]
PATCHER = REPO_ROOT / "tools" / "patch_emby_watchparty_progress.py"


def load_patcher():
    spec = importlib.util.spec_from_file_location("emby_watchparty_progress_patch", PATCHER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


PLAYBACKMANAGER_FIXTURE = (
    "define(function(){"
    "function sendProgressUpdate(instance,player,progressEventName,reportPlaylist,additionalData,isAutomated){"
    "return reportProgress(instance,player,progressEventName,additionalData)}"
    "function changeStream(player,ticks,params,progressEventName){return player.currentTime(ticks)}"
    "function PlaybackManager(){}"
    "var self={_currentPlayer:null};"
    "self.seek=function(ticks,player){return ticks=Math.max(0,ticks),(player=player||self._currentPlayer)&&!enableLocalPlaylistManagement(player)?player.isLocalPlayer?player.seek((ticks||0)/1e4):player.seek(ticks):changeStream(player,ticks)};"
    "function onPlaybackTimeUpdate(e){sendProgressUpdate(self,this,\"timeupdate\")}"
    "});"
)


class EmbyWatchPartyProgressPatchTests(unittest.TestCase):
    def _write_fixture(self, root):
        module_dir = root / "modules" / "common" / "playback"
        module_dir.mkdir(parents=True)
        playbackmanager = module_dir / "playbackmanager.js"
        playbackmanager.write_text(PLAYBACKMANAGER_FIXTURE, encoding="utf-8")
        return playbackmanager

    def test_seek_sends_explicit_watch_party_progress_with_target(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            playbackmanager = self._write_fixture(root)

            result = subprocess.run(
                [sys.executable, str(PATCHER), "--dashboard-root", str(root)],
                check=False,
                capture_output=True,
                text=True,
            )

            self.assertEqual(0, result.returncode, result.stderr)
            patched = playbackmanager.read_text(encoding="utf-8")
            self.assertIn('WatchParty/Playback/Seek', patched)
            self.assertIn('PositionTicks:ticks', patched)
            self.assertIn('DeviceId:apiClient.deviceId()', patched)
            self.assertIn('markWatchPartySeek', patched)
            self.assertLess(
                patched.index('result=player&&!enableLocalPlaylistManagement(player)'),
                patched.index('markWatchPartySeek(self,player,ticks)'),
            )

    def test_patch_is_idempotent_and_rejects_unknown_dashboard_shape(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            playbackmanager = self._write_fixture(root)
            command = [sys.executable, str(PATCHER), "--dashboard-root", str(root)]

            first = subprocess.run(command, check=False, capture_output=True, text=True)
            first_contents = playbackmanager.read_bytes()
            second = subprocess.run(command, check=False, capture_output=True, text=True)

            self.assertEqual(0, first.returncode, first.stderr)
            self.assertEqual("patched\n", first.stdout)
            self.assertEqual(0, second.returncode, second.stderr)
            self.assertEqual("already-patched\n", second.stdout)
            self.assertEqual(first_contents, playbackmanager.read_bytes())

            playbackmanager.write_text("define(function(){})", encoding="utf-8")
            incompatible = subprocess.run(command, check=False, capture_output=True, text=True)
            self.assertNotEqual(0, incompatible.returncode)


if __name__ == "__main__":
    unittest.main()
