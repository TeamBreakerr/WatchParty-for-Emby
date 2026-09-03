import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[1]
PATCHER = REPO_ROOT / "tools" / "patch_emby_watchparty_progress.py"
INSTALLER = REPO_ROOT / "tools" / "install_emby_watchparty_progress_patch.sh"
WARMUP = REPO_ROOT / "tools" / "emby_ass_font_warmup.ass"
TIMER = REPO_ROOT / "deploy" / "emby-watchparty-progress-patch.timer"


def load_patcher():
    spec = importlib.util.spec_from_file_location("emby_watchparty_progress_patch", PATCHER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


PLAYBACKMANAGER_FIXTURE = (
    "define(function(){"
    "var _connectionmanager={default:{getApiClient:function(item){return item.apiClient}}};"
    "function normalizePlayOptions(){}"
    "function getPlayerData(player){return player.__playbackData||(player.__playbackData={})}"
    "function reportProgress(){}"
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
    "var self={_currentPlayer:null,getTranscodingFallbackOptions:function(){return {canTrigger:false}}};"
    "function onPlaybackStopped(e,info){return info.returnPromise?Promise.reject(info):void 0}"
    "function setSrcIntoPlayer(apiClient,player,streamInfo,progressEventName,previousPlaySessionId,signal){return normalizePlayOptions(streamInfo),getPlayerData(player).streamInfo=streamInfo,player.play(streamInfo,signal).then(function(){streamInfo.started=!0,\"subtitletrackchange\"===progressEventName||\"audiotrackchange\"===progressEventName?_events.default.trigger(player,progressEventName):sendProgressUpdate(self,player,progressEventName||\"timeupdate\"),previousPlaySessionId&&apiClient.stopActiveEncodings(previousPlaySessionId)},function(err){return console.log(\"setSrcIntoPlayer error: \"+(null==err?void 0:err.toString())),previousPlaySessionId&&apiClient.stopActiveEncodings(previousPlaySessionId),streamInfo.started=!1,onPlaybackError.call(player,err,{type:err&&err.name?err.name:\"mediadecodeerror\",streamInfo:streamInfo,returnPromise:!0})})}"
    "function onPlaybackError(e,error){var errorType=error.type,errorType=(console.log(\"playbackmanager playback error type: \"+(errorType||\"\")),error.streamInfo||getPlayerData(this).streamInfo);if(errorType){var transcodingFallbackOptions=self.getTranscodingFallbackOptions(this,error);if(transcodingFallbackOptions.canTrigger)return changeStream(this,getCurrentTicks(this)||errorType.playerStartPositionTicks,{EnableDirectPlay:!1,EnableDirectStream:!1,AllowVideoStreamCopy:\"Transcode\"!==errorType.playMethod&&null,AllowAudioStreamCopy:!transcodingFallbackOptions.currentlyPreventsAudioStreamCopy&&!transcodingFallbackOptions.currentlyPreventsVideoStreamCopy&&null})}return onPlaybackStopped.call(this,e,{errorCode:\"NoCompatibleStream\",returnPromise:error.returnPromise})}"
    "globalThis.__setSrcIntoPlayer=setSrcIntoPlayer;"
    "globalThis.__getPlayerData=getPlayerData;"
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

    def _evaluate_hls_retry(
        self,
        playbackmanager,
        switch_stream=False,
        always_fail=False,
        abort_during_retry=False,
        abort_error=False,
    ):
        if abort_error:
            play_result = (
                "const error=new Error('aborted');error.name='AbortError';"
                "return Promise.reject(error);"
            )
        elif always_fail:
            play_result = "return Promise.reject(new Error('cold'));"
        else:
            play_result = (
                "return calls.length===1?Promise.reject(new Error('cold')):"
                "Promise.resolve();"
            )
        script = (
            "globalThis.define=function(factory){factory()};"
            + playbackmanager.read_text(encoding="utf-8")
            + "const calls=[];const controller=new AbortController();"
            + "const apiClient={stopActiveEncodings:function(){}};"
            + "const streamInfo={url:'/Videos/item/master.m3u8?x=1',playMethod:'Transcode',"
            + "playSessionId:'same-session',item:{apiClient:apiClient}};"
            + "const player={play:function(info,signal){calls.push({info:info,signal:signal});"
            + play_result
            + "}};"
            + ("setTimeout(function(){globalThis.__getPlayerData(player).streamInfo={url:'replacement'}},50);"
               if switch_stream else "")
            + ("setTimeout(function(){controller.abort()},50);"
               if abort_during_retry else "")
            + "globalThis.__setSrcIntoPlayer(apiClient,player,streamInfo,null,null,controller.signal).then(function(){"
            + "process.stdout.write(JSON.stringify({calls:calls.length,same:calls.every(function(value){return value.info===streamInfo}),sameSignal:calls.every(function(value){return value.signal===controller.signal}),session:streamInfo.playSessionId}))"
            + "}).catch(function(){process.stdout.write(JSON.stringify({calls:calls.length,same:calls.every(function(value){return value.info===streamInfo}),sameSignal:calls.every(function(value){return value.signal===controller.signal}),session:streamInfo.playSessionId,rejected:true}))});"
        )
        result = subprocess.run(
            ["node", "-e", script],
            check=False,
            capture_output=True,
            text=True,
        )
        self.assertEqual(0, result.returncode, result.stderr)
        return json.loads(result.stdout[result.stdout.rfind("{"):])

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

    def test_patch_leaves_native_media_selection_and_hls_error_handling_unchanged(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            playbackmanager = self._write_fixture(root)
            original = playbackmanager.read_text(encoding="utf-8")
            patcher = load_patcher()

            result = subprocess.run(
                [sys.executable, str(PATCHER), "--dashboard-root", str(root)],
                check=False,
                capture_output=True,
                text=True,
            )

            self.assertEqual(0, result.returncode, result.stderr)
            patched = playbackmanager.read_text(encoding="utf-8")
            self.assertIn(patcher.STREAM_URL_SELECTION_ORIGINAL, patched)
            self.assertIn(patcher.DIRECT_STREAM_SELECTION_ORIGINAL, patched)
            self.assertIn(patcher.SET_SRC_INTO_PLAYER_ORIGINAL, patched)
            self.assertIn(patcher.PLAYBACK_ERROR_ORIGINAL, patched)
            self.assertNotIn("retryWatchPartyHlsStartup", patched)
            self.assertEqual(
                original.count(patcher.STREAM_URL_SELECTION_ORIGINAL),
                patched.count(patcher.STREAM_URL_SELECTION_ORIGINAL),
            )

    def test_installer_prewarms_ass_fonts_once_per_container_start(self):
        installer = INSTALLER.read_text(encoding="utf-8")
        warmup = WARMUP.read_text(encoding="utf-8")
        timer = TIMER.read_text(encoding="utf-8")

        self.assertIn("{{.State.StartedAt}}", installer)
        self.assertIn("ass-font-warmup.started-at", installer)
        self.assertIn("subtitles=$warmup_path:fontsdir=/config/fonts", installer)
        self.assertIn("-frames:v 1", installer)
        self.assertLess(
            installer.index("ass-font-warmup.started-at"),
            installer.index('if [ "$patch_output" = "already-patched" ]'),
        )
        self.assertIn("ScriptType: v4.00+", warmup)
        self.assertIn("Go Noto Kurrent", warmup)
        self.assertIn("OnBootSec=1min", timer)
        self.assertIn("OnUnitActiveSec=5min", timer)

    def test_hls_cold_start_uses_native_error_path_without_retry(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            playbackmanager = self._patch_fixture(root)
            result = self._evaluate_hls_retry(playbackmanager)

            self.assertEqual(1, result["calls"])
            self.assertTrue(result["same"])
            self.assertTrue(result["sameSignal"])
            self.assertEqual("same-session", result["session"])
            self.assertTrue(result["rejected"])

    def test_virtual_strm_without_direct_url_keeps_native_selection(self):
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

            self.assertEqual("DirectStream", stream_info["playMethod"])
            self.assertEqual(
                "server:Videos/item-1/stream.strm",
                stream_info["url"],
            )
            self.assertEqual("video/strm", stream_info["mimeType"])

    def test_virtual_strm_stream_url_keeps_native_selection(self):
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
                "/Videos/item-1/stream.strm?static=true",
                stream_info["url"],
            )

    def test_probed_mp4_strm_path_keeps_native_stream_url(self):
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

            self.assertEqual("Transcode", stream_info["playMethod"])
            self.assertEqual(
                "/Videos/item-1/stream.strm?static=true",
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
                "/Videos/item-1/stream.strm?static=true",
                stream_info["url"],
            )
            self.assertEqual("video/mp4", stream_info["mimeType"])

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
            self.assertIn('mediaSource.SupportsDirectStream?(', updated)

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
            self.assertIn(':mediaSource.StreamUrl?(', updated)

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
