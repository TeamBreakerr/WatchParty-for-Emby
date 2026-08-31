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
    "function createStreamInfo(type){var mediaSourceContainer,prefix;"
    "prefix=\"Video\"===type?\"Videos\":\"Audio\","
    "mediaSourceContainer=mediaSourceContainer.toLowerCase().replace(\"m4v\",\"mp4\"),"
    "apiClient.getUrl(prefix+\"/stream.\"+mediaSourceContainer)}"
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
            self.assertIn(
                'PlaySessionId:state&&state.PlayState&&state.PlayState.PlaySessionId',
                patched,
            )
            self.assertIn('DeviceId:apiClient.deviceId()', patched)
            self.assertIn('markWatchPartySeek', patched)
            self.assertIn('state=instance.getPlayerState?instance.getPlayerState(player):null', patched)
            self.assertIn('item=item&&item.ServerId?item:(state&&state.NowPlayingItem)||item', patched)
            self.assertNotIn('!item.ServerId))return', patched)
            self.assertLess(
                patched.index('markWatchPartySeek(self,player,ticks)'),
                patched.index('result=player&&!enableLocalPlaylistManagement(player)'),
            )

    def test_strm_video_container_is_left_for_emby_to_resolve(self):
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
            self.assertIn(
                'mediaSourceContainer=mediaSourceContainer.toLowerCase().replace("m4v","mp4"),'
                'apiClient.getUrl',
                patched,
            )
            self.assertNotIn(
                '"strm"===mediaSourceContainer&&"Video"===type&&(mediaSourceContainer="mp4",contentType="video/mp4")',
                patched,
            )

    def test_patch_migrates_the_legacy_global_strm_to_mp4_override(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            playbackmanager = self._write_fixture(root)
            patcher = load_patcher()
            playbackmanager.write_text(
                PLAYBACKMANAGER_FIXTURE.replace(
                    patcher.DIRECT_STREAM_CONTAINER_ORIGINAL,
                    patcher.LEGACY_DIRECT_STREAM_CONTAINER_PATCHED,
                    1,
                ),
                encoding="utf-8",
            )

            result = subprocess.run(
                [sys.executable, str(PATCHER), "--dashboard-root", str(root)],
                check=False,
                capture_output=True,
                text=True,
            )

            self.assertEqual(0, result.returncode, result.stderr)
            migrated = playbackmanager.read_text(encoding="utf-8")
            self.assertIn(patcher.DIRECT_STREAM_CONTAINER_ORIGINAL, migrated)
            self.assertNotIn(patcher.LEGACY_DIRECT_STREAM_CONTAINER_PATCHED, migrated)

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

    def test_patch_migrates_previous_helper_to_include_playback_generation(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            playbackmanager = self._write_fixture(root)
            patcher = load_patcher()
            playbackmanager.write_text(
                PLAYBACKMANAGER_FIXTURE.replace(
                    "function PlaybackManager(){",
                    patcher.PLAYBACK_MANAGER_START_PATCHED_WITHOUT_PLAY_SESSION_ID,
                    1,
                ),
                encoding="utf-8",
            )
            result = subprocess.run(
                [sys.executable, str(PATCHER), "--dashboard-root", str(root)],
                check=False,
                capture_output=True,
                text=True,
            )

            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual("patched\n", result.stdout)
            current = playbackmanager.read_text(encoding="utf-8")
            self.assertIn(
                'PlaySessionId:state&&state.PlayState&&state.PlayState.PlaySessionId',
                current,
            )
            self.assertNotIn(
                patcher.PLAYBACK_MANAGER_START_PATCHED_WITHOUT_PLAY_SESSION_ID,
                current,
            )


if __name__ == "__main__":
    unittest.main()
