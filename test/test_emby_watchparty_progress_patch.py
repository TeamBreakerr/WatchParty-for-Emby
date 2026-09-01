import importlib.util
import json
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
    "function getMimeType(type,container){return type+\"/\"+container}"
    "function createStreamInfo(apiClient,type,item,mediaSource){"
    "var playMethod=\"Transcode\",directOptions,prefix,mediaSourceContainer,mediaUrl,contentType;"
    "return mediaSourceContainer=(mediaSource.Container||\"\").toLowerCase(),"
    "contentType=getMimeType(type.toLowerCase(),mediaSourceContainer),"
    "mediaSource.enableDirectPlay?(mediaUrl=mediaSource.Path,playMethod=\"DirectPlay\")"
    ":mediaSource.StreamUrl?(playMethod=\"Transcode\",mediaUrl=mediaSource.StreamUrl)"
    ":mediaSource.SupportsDirectStream?(mediaUrl=mediaSource.DirectStreamUrl"
    "?apiClient.getUrl(mediaSource.DirectStreamUrl)"
    ":(directOptions={Static:!0,mediaSourceId:mediaSource.Id},"
    "prefix=\"Video\"===type?\"Videos\":\"Audio\","
    "mediaSourceContainer=mediaSourceContainer.toLowerCase().replace(\"m4v\",\"mp4\"),"
    "apiClient.getUrl(prefix+\"/\"+item.Id+\"/stream.\"+mediaSourceContainer,directOptions)),"
    "playMethod=\"DirectStream\")"
    ":mediaSource.SupportsTranscoding&&(mediaUrl=apiClient.getUrl(mediaSource.TranscodingUrl),"
    "\"hls\"===mediaSource.TranscodingSubProtocol"
    "?contentType=\"application/x-mpegURL\""
    ":contentType=getMimeType(type.toLowerCase(),mediaSource.TranscodingContainer)),"
    "!mediaUrl&&mediaSource.SupportsDirectPlay"
    "&&(mediaUrl=mediaSource.Path,playMethod=\"DirectPlay\"),"
    "{url:mediaUrl,mimeType:contentType,playMethod:playMethod}}"
    "globalThis.__createStreamInfo=createStreamInfo;"
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

    def _patch_fixture(self, root):
        playbackmanager = self._write_fixture(root)
        result = subprocess.run(
            [sys.executable, str(PATCHER), "--dashboard-root", str(root)],
            check=False,
            capture_output=True,
            text=True,
        )
        self.assertEqual(0, result.returncode, result.stderr)
        return playbackmanager

    def _evaluate_stream_info(self, playbackmanager, media_source):
        script = (
            "globalThis.define=function(factory){factory()};"
            + playbackmanager.read_text(encoding="utf-8")
            + "const apiClient={getUrl:function(path){return 'server:'+path}};"
            + "const result=globalThis.__createStreamInfo("
            + "apiClient,'Video',{Id:'item-1'},"
            + json.dumps(media_source)
            + ");process.stdout.write(JSON.stringify(result));"
        )
        result = subprocess.run(
            ["node", "-e", script],
            check=False,
            capture_output=True,
            text=True,
        )
        self.assertEqual(0, result.returncode, result.stderr)
        return json.loads(result.stdout)

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

    def test_virtual_strm_without_direct_url_uses_server_transcoding_url(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            playbackmanager = self._patch_fixture(root)
            stream_info = self._evaluate_stream_info(
                playbackmanager,
                {
                    "Id": "source-1",
                    "Container": "strm",
                    "SupportsDirectStream": True,
                    "SupportsTranscoding": True,
                    "TranscodingUrl": "/Videos/item-1/master.m3u8",
                    "TranscodingSubProtocol": "hls",
                    "MediaStreams": [],
                },
            )

            self.assertEqual("Transcode", stream_info["playMethod"])
            self.assertEqual(
                "server:/Videos/item-1/master.m3u8",
                stream_info["url"],
            )
            self.assertEqual("application/x-mpegURL", stream_info["mimeType"])

    def test_virtual_strm_stream_url_that_targets_strm_uses_server_transcoding_url(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            playbackmanager = self._patch_fixture(root)
            stream_info = self._evaluate_stream_info(
                playbackmanager,
                {
                    "Id": "source-1",
                    "Container": "strm",
                    "StreamUrl": "/Videos/item-1/stream.strm?static=true",
                    "SupportsDirectStream": True,
                    "SupportsTranscoding": True,
                    "TranscodingUrl": "/Videos/item-1/master.m3u8",
                    "TranscodingSubProtocol": "hls",
                    "MediaStreams": [],
                },
            )

            self.assertEqual("Transcode", stream_info["playMethod"])
            self.assertEqual(
                "server:/Videos/item-1/master.m3u8",
                stream_info["url"],
            )

    def test_probed_mp4_strm_path_rejects_invalid_stream_url(self):
        """Xiaoya STRM paths expose the probed container, not ``strm``.

        The media source can therefore be ``Container=mp4`` while its supplied
        ``StreamUrl`` still ends in ``stream.strm``.  The URL is the invalid
        output choice; rejecting it lets Emby Web construct ``stream.mp4`` from
        the server-probed container without globally forcing every STRM to MP4.
        """
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            playbackmanager = self._patch_fixture(root)
            stream_info = self._evaluate_stream_info(
                playbackmanager,
                {
                    "Id": "source-1",
                    "Path": "/media/library/episode.strm",
                    "Container": "mp4",
                    "StreamUrl": "/Videos/item-1/stream.strm?static=true",
                    "SupportsDirectStream": True,
                    "SupportsTranscoding": True,
                    "TranscodingUrl": "/Videos/item-1/master.m3u8",
                    "TranscodingSubProtocol": "hls",
                    "MediaStreams": [],
                },
            )

            self.assertEqual("DirectStream", stream_info["playMethod"])
            self.assertEqual(
                "server:Videos/item-1/stream.mp4",
                stream_info["url"],
            )

    def test_probed_mp4_strm_path_can_fall_back_to_server_transcoding(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            playbackmanager = self._patch_fixture(root)
            stream_info = self._evaluate_stream_info(
                playbackmanager,
                {
                    "Id": "source-1",
                    "Path": "/media/library/episode.strm",
                    "Container": "mp4",
                    "StreamUrl": "/Videos/item-1/stream.strm?static=true",
                    "SupportsDirectStream": False,
                    "SupportsTranscoding": True,
                    "TranscodingUrl": "/Videos/item-1/master.m3u8",
                    "TranscodingSubProtocol": "hls",
                    "MediaStreams": [],
                },
            )

            self.assertEqual("Transcode", stream_info["playMethod"])
            self.assertEqual(
                "server:/Videos/item-1/master.m3u8",
                stream_info["url"],
            )
            self.assertEqual("application/x-mpegURL", stream_info["mimeType"])

    def test_virtual_strm_keeps_a_valid_server_stream_url(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            playbackmanager = self._patch_fixture(root)
            stream_info = self._evaluate_stream_info(
                playbackmanager,
                {
                    "Id": "source-1",
                    "Container": "strm",
                    "StreamUrl": "/Videos/item-1/master.m3u8",
                    "SupportsDirectStream": True,
                    "SupportsTranscoding": True,
                    "TranscodingUrl": "/Videos/item-1/fallback.m3u8",
                    "TranscodingSubProtocol": "hls",
                    "MediaStreams": [],
                },
            )

            self.assertEqual("Transcode", stream_info["playMethod"])
            self.assertEqual(
                "/Videos/item-1/master.m3u8",
                stream_info["url"],
            )

    def test_virtual_strm_prefers_an_explicit_direct_stream_url(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            playbackmanager = self._patch_fixture(root)
            stream_info = self._evaluate_stream_info(
                playbackmanager,
                {
                    "Id": "source-1",
                    "Container": "strm",
                    "DirectStreamUrl": "/Videos/item-1/stream.mkv",
                    "SupportsDirectStream": True,
                    "SupportsTranscoding": True,
                    "TranscodingUrl": "/Videos/item-1/master.m3u8",
                    "MediaStreams": [],
                },
            )

            self.assertEqual("DirectStream", stream_info["playMethod"])
            self.assertEqual(
                "server:/Videos/item-1/stream.mkv",
                stream_info["url"],
            )

    def test_real_media_container_keeps_native_direct_stream_fallback(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            playbackmanager = self._patch_fixture(root)
            stream_info = self._evaluate_stream_info(
                playbackmanager,
                {
                    "Id": "source-1",
                    "Container": "mkv",
                    "SupportsDirectStream": True,
                    "SupportsTranscoding": True,
                    "TranscodingUrl": "/Videos/item-1/master.m3u8",
                    "MediaStreams": [],
                },
            )

            self.assertEqual("DirectStream", stream_info["playMethod"])
            self.assertEqual(
                "server:Videos/item-1/stream.mkv",
                stream_info["url"],
            )

    def test_patch_migrates_an_existing_static_strm_mp4_mapping(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            playbackmanager = self._write_fixture(root)
            patcher = load_patcher()
            playbackmanager.write_text(
                PLAYBACKMANAGER_FIXTURE.replace(
                    patcher.DIRECT_STREAM_CONTAINER_ORIGINAL,
                    patcher.LEGACY_STRM_MP4_MAPPING,
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
            updated = playbackmanager.read_text(encoding="utf-8")
            self.assertNotIn(patcher.LEGACY_STRM_MP4_MAPPING, updated)
            self.assertNotIn('mediaSourceContainer="mp4",contentType="video/mp4"', updated)
            self.assertIn(
                'mediaSource.SupportsDirectStream&&('
                '"strm"!==mediaSourceContainer||mediaSource.DirectStreamUrl)?',
                updated,
            )

    def test_patch_migrates_the_container_gated_stream_url_guard(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            playbackmanager = self._write_fixture(root)
            old_guard = (
                ':mediaSource.StreamUrl&&'
                '!("strm"===mediaSourceContainer&&'
                '/\\.strm(?:[?#]|$)/i.test(mediaSource.StreamUrl))?('
            )
            playbackmanager.write_text(
                PLAYBACKMANAGER_FIXTURE.replace(
                    ':mediaSource.StreamUrl?(',
                    old_guard,
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
            updated = playbackmanager.read_text(encoding="utf-8")
            self.assertNotIn(old_guard, updated)
            self.assertIn(
                ':mediaSource.StreamUrl&&!/\\.strm(?:[?#]|$)/i.test(mediaSource.StreamUrl)?(',
                updated,
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
