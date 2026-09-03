#!/usr/bin/env python3
"""Report explicit Emby Web seeks to WatchParty without changing playback.

Emby Web normally reports a seek only as a later ``timeupdate``.  This patch
keeps the native player behavior intact while sending one authenticated request
to the plugin's dedicated seek endpoint with the requested target.  The server
can therefore distinguish a user drag from ordinary progress heartbeats.

It also refuses a StreamUrl that still ends in ``stream.strm``.  That is not a
room-scoped behavior change but a correctness one: ``.strm`` is a text pointer,
so Emby ends up asking ffmpeg to write a ``.strm`` output container, which
fails instantly and surfaces as "no compatible stream".

Older revisions went further - forcing a virtual STRM container to MP4 and
retrying a failed HLS startup.  Those did affect ordinary playback and could
keep an abandoned transcoding stream alive, so this patch migrates them back to
Emby's native behavior; the server endpoint independently checks that an
explicit seek belongs to an active room before applying it.
"""

import argparse
import os
import stat
import sys
import tempfile
from pathlib import Path


PLAYBACK_MANAGER_MODULE = "modules/common/playback/playbackmanager.js"
PLAYBACK_MANAGER_START = "function PlaybackManager(){"
PLAYBACK_MANAGER_START_PATCHED = (
    'function markWatchPartySeek(instance,player,ticks){var item,apiClient,state;'
    'if(!player||!instance)return;item=instance.currentItem?instance.currentItem(player):null;'
    'state=instance.getPlayerState?instance.getPlayerState(player):null;'
    'item=item&&item.ServerId?item:(state&&state.NowPlayingItem)||item;'
    'if(!item||!item.Id)return;'
    'apiClient=_connectionmanager.default.getApiClient(item);'
    'apiClient&&apiClient.ajax&&apiClient.ajax({type:"POST",url:apiClient.getUrl('
    '"WatchParty/Playback/Seek"),data:JSON.stringify({PositionTicks:ticks,ItemId:item.Id,'
    'PlaySessionId:state&&state.PlayState&&state.PlayState.PlaySessionId,'
    'DeviceId:apiClient.deviceId()}),contentType:"application/json"}).catch(function(err){'
    'console.warn("WatchParty seek notification failed",err)})}'
    "function PlaybackManager(){"
)
PLAYBACK_MANAGER_START_PATCHED_WITHOUT_PLAY_SESSION_ID = PLAYBACK_MANAGER_START_PATCHED.replace(
    'PlaySessionId:state&&state.PlayState&&state.PlayState.PlaySessionId,',
    '',
    1,
)
PLAYBACK_MANAGER_START_PATCHED_LEGACY = (
    'function markWatchPartySeek(instance,player,ticks){var item,apiClient;'
    'if(!player||!instance||!instance.currentItem||(item=instance.currentItem(player),'
    '!item||!item.Id||!item.ServerId))return;apiClient=_connectionmanager.default.getApiClient(item);'
    'apiClient&&apiClient.ajax&&apiClient.ajax({type:"POST",url:apiClient.getUrl('
    '"WatchParty/Playback/Seek"),data:JSON.stringify({PositionTicks:ticks,ItemId:item.Id,'
    'DeviceId:apiClient.deviceId()}),contentType:"application/json"}).catch(function(err){'
    'console.warn("WatchParty seek notification failed",err)})}'
    "function PlaybackManager(){"
)
PLAYBACK_MANAGER_START_PATCHED_INTERMEDIATE = (
    'function markWatchPartySeek(instance,player,ticks){var item,apiClient,state;'
    'if(!player||!instance)return;item=instance.currentItem?instance.currentItem(player):null;'
    'state=!item&&instance.getPlayerState?instance.getPlayerState(player):null;'
    'item=item||(state&&state.NowPlayingItem);if(!item||!item.Id)return;'
    'apiClient=_connectionmanager.default.getApiClient(item);'
    'apiClient&&apiClient.ajax&&apiClient.ajax({type:"POST",url:apiClient.getUrl('
    '"WatchParty/Playback/Seek"),data:JSON.stringify({PositionTicks:ticks,ItemId:item.Id,'
    'DeviceId:apiClient.deviceId()}),contentType:"application/json"}).catch(function(err){'
    'console.warn("WatchParty seek notification failed",err)})}'
    "function PlaybackManager(){"
)
SEEK_ORIGINAL = (
    'self.seek=function(ticks,player){return ticks=Math.max(0,ticks),'
    '(player=player||self._currentPlayer)&&!enableLocalPlaylistManagement(player)'
    '?player.isLocalPlayer?player.seek((ticks||0)/1e4):player.seek(ticks)'
    ':changeStream(player,ticks)}'
)
SEEK_PATCHED = (
    'self.seek=function(ticks,player){var result;return ticks=Math.max(0,ticks),'
    'player=player||self._currentPlayer,markWatchPartySeek(self,player,ticks),'
    'result=player&&!enableLocalPlaylistManagement(player)'
    '?player.isLocalPlayer?player.seek((ticks||0)/1e4):player.seek(ticks)'
    ':changeStream(player,ticks),result}'
)
SEEK_PATCHED_LEGACY = (
    'self.seek=function(ticks,player){var result;return ticks=Math.max(0,ticks),'
    'player=player||self._currentPlayer,result=player&&!enableLocalPlaylistManagement(player)'
    '?player.isLocalPlayer?player.seek((ticks||0)/1e4):player.seek(ticks)'
    ':changeStream(player,ticks),markWatchPartySeek(self,player,ticks),result}'
)

# Legacy versions of this patch replaced a virtual STRM container with MP4.
# Keep both strings solely so an already-patched dashboard can be migrated back
# to Emby's original normalization before installing the URL-selection guard.
DIRECT_STREAM_CONTAINER_ORIGINAL = (
    'mediaSourceContainer=mediaSourceContainer.toLowerCase().replace("m4v","mp4"),'
)
LEGACY_STRM_MP4_MAPPING = (
    'mediaSourceContainer=mediaSourceContainer.toLowerCase().replace("m4v","mp4"),'
    '"strm"===mediaSourceContainer&&"Video"===type&&(mediaSourceContainer="mp4",contentType="video/mp4"),'
)

# A `.strm` path is a text pointer, never a playable output format.  Emby can
# still hand Emby Web a StreamUrl of `/Videos/{id}/stream.strm` after probing
# the indirection, and Emby then derives the transcode output container from
# that extension: ffmpeg is asked to write a `.strm` file, reports
# `Unable to find a suitable output format`, and exits immediately, so the
# first HLS segment answers 500 and the client reports that no compatible
# stream exists.  Reject such a URL by its own extension, independently of
# Container - the probe normally replaces Container with the real remote one.
# The Direct Stream branch then builds stream.{probed-container}, and if
# Container itself is still strm its own guard falls through to TranscodingUrl.
STREAM_URL_SELECTION_ORIGINAL = ':mediaSource.StreamUrl?('
STREAM_URL_SELECTION_PATCHED_LEGACY = (
    ':mediaSource.StreamUrl&&'
    '!("strm"===mediaSourceContainer&&'
    '/\\.strm(?:[?#]|$)/i.test(mediaSource.StreamUrl))?('
)
STREAM_URL_SELECTION_PATCHED = (
    ':mediaSource.StreamUrl&&'
    '!/\\.strm(?:[?#]|$)/i.test(mediaSource.StreamUrl)?('
)
# The same rule on the Direct Stream branch: with Container still "strm" and no
# DirectStreamUrl, Emby Web would build stream.strm itself. Fall through to the
# server's TranscodingUrl instead.
DIRECT_STREAM_SELECTION_ORIGINAL = 'mediaSource.SupportsDirectStream?('
DIRECT_STREAM_SELECTION_PATCHED = (
    'mediaSource.SupportsDirectStream&&'
    '("strm"!==mediaSourceContainer||mediaSource.DirectStreamUrl)?('
)

# Older revisions retried a failed HLS player.play() call.  Retain the legacy
# shapes only for removal: the retry could outlive a cancelled player, so the
# installed dashboard now uses Emby's native startup and cancellation path.
SET_SRC_INTO_PLAYER_ORIGINAL = (
    'function setSrcIntoPlayer(apiClient,player,streamInfo,progressEventName,'
    'previousPlaySessionId,signal){return normalizePlayOptions(streamInfo),'
    'getPlayerData(player).streamInfo=streamInfo,'
)
SET_SRC_INTO_PLAYER_PATCHED = (
    'function retryWatchPartyHlsStartup(player,streamInfo,error){var signal=streamInfo&&'
    'streamInfo.watchPartyHlsStartupSignal,apiClient=streamInfo&&'
    'streamInfo.watchPartyHlsStartupApiClient;if(!streamInfo||'
    '"Transcode"!==streamInfo.playMethod||'
    '!/\\.m3u8(?:[?#]|$)/i.test(streamInfo.url||"")||'
    'error&&("AbortError"===error.name||"AbortError"===error.type)||'
    'signal&&signal.aborted||'
    'streamInfo.watchPartyHlsStartupRetried||'
    'Date.now()-(streamInfo.watchPartyHlsStartupStartedAt||0)>15e3||'
    'getPlayerData(player).streamInfo!==streamInfo||!apiClient)return null;'
    'return streamInfo.watchPartyHlsStartupRetried=!0,new Promise(function(resolve){'
    'setTimeout(resolve,350)}).then(function(){'
    'if(signal&&signal.aborted)return Promise.reject(signal.reason||{name:"AbortError"});'
    'if(getPlayerData(player).streamInfo===streamInfo)'
    'return setSrcIntoPlayer(apiClient,player,streamInfo,'
    'streamInfo.watchPartyHlsStartupProgressEventName,null,signal)})}'
    'function setSrcIntoPlayer(apiClient,player,streamInfo,progressEventName,'
    'previousPlaySessionId,signal){return normalizePlayOptions(streamInfo),'
    '"Transcode"===streamInfo.playMethod&&'
    '/\\.m3u8(?:[?#]|$)/i.test(streamInfo.url||"")&&('
    'streamInfo.watchPartyHlsStartupStartedAt=Date.now(),'
    'streamInfo.watchPartyHlsStartupApiClient=apiClient,'
    'streamInfo.watchPartyHlsStartupProgressEventName=progressEventName,'
    'streamInfo.watchPartyHlsStartupSignal=signal),'
    'getPlayerData(player).streamInfo=streamInfo,'
)
PLAYBACK_ERROR_ORIGINAL = (
    'function onPlaybackError(e,error){var errorType=error.type,errorType=('
    'console.log("playbackmanager playback error type: "+(errorType||"")),'
    'error.streamInfo||getPlayerData(this).streamInfo);if(errorType){'
)
PLAYBACK_ERROR_PATCHED = (
    'function onPlaybackError(e,error){var hlsRetry,errorType=error.type,errorType=('
    'console.log("playbackmanager playback error type: "+(errorType||"")),'
    'error.streamInfo||getPlayerData(this).streamInfo);'
    'if(hlsRetry=retryWatchPartyHlsStartup(this,errorType,e),hlsRetry)return hlsRetry;'
    'if(errorType){'
)


def patch_text(text: str, original: str, patched: str, source: Path) -> tuple[str, bool]:
    original_count = text.count(original)
    patched_count = text.count(patched)
    if original_count + patched_count != 1:
        raise RuntimeError(
            f"expected exactly one original or patched point in {source} "
            f"(original={original_count}, patched={patched_count})"
        )
    if patched_count:
        return text, False
    return text.replace(original, patched, 1), True


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
    playbackmanager = dashboard_root / PLAYBACK_MANAGER_MODULE
    text = playbackmanager.read_text(encoding="utf-8")
    if PLAYBACK_MANAGER_START_PATCHED in text:
        helper_changed = False
    elif PLAYBACK_MANAGER_START_PATCHED_WITHOUT_PLAY_SESSION_ID in text:
        text = text.replace(
            PLAYBACK_MANAGER_START_PATCHED_WITHOUT_PLAY_SESSION_ID,
            PLAYBACK_MANAGER_START_PATCHED,
            1,
        )
        helper_changed = True
    elif PLAYBACK_MANAGER_START_PATCHED_INTERMEDIATE in text:
        text = text.replace(
            PLAYBACK_MANAGER_START_PATCHED_INTERMEDIATE,
            PLAYBACK_MANAGER_START_PATCHED,
            1,
        )
        helper_changed = True
    elif PLAYBACK_MANAGER_START_PATCHED_LEGACY in text:
        text = text.replace(
            PLAYBACK_MANAGER_START_PATCHED_LEGACY,
            PLAYBACK_MANAGER_START_PATCHED,
            1,
        )
        helper_changed = True
    else:
        text, helper_changed = patch_text(
            text,
            PLAYBACK_MANAGER_START,
            PLAYBACK_MANAGER_START_PATCHED,
            playbackmanager,
        )
    if SEEK_PATCHED in text:
        seek_changed = False
    elif SEEK_PATCHED_LEGACY in text:
        text = text.replace(SEEK_PATCHED_LEGACY, SEEK_PATCHED, 1)
        seek_changed = True
    else:
        text, seek_changed = patch_text(
            text,
            SEEK_ORIGINAL,
            SEEK_PATCHED,
            playbackmanager,
        )

    legacy_container_count = text.count(LEGACY_STRM_MP4_MAPPING)
    if legacy_container_count > 1:
        raise RuntimeError(
            f"expected at most one legacy STRM-to-MP4 patch in {playbackmanager} "
            f"(legacy={legacy_container_count})"
        )
    if legacy_container_count:
        text = text.replace(
            LEGACY_STRM_MP4_MAPPING,
            DIRECT_STREAM_CONTAINER_ORIGINAL,
            1,
        )
        container_changed = True
    else:
        container_changed = False

    text, selection_changed = patch_text(
        text,
        DIRECT_STREAM_SELECTION_ORIGINAL,
        DIRECT_STREAM_SELECTION_PATCHED,
        playbackmanager,
    )
    legacy_stream_url_count = text.count(STREAM_URL_SELECTION_PATCHED_LEGACY)
    if legacy_stream_url_count > 1:
        raise RuntimeError(
            f"expected at most one legacy container-gated StreamUrl patch in "
            f"{playbackmanager} (legacy={legacy_stream_url_count})"
        )
    if legacy_stream_url_count:
        # The legacy shape only rejected stream.strm while Container was still
        # "strm"; the probe usually replaces Container, so it let the unplayable
        # URL through. Migrate it to the extension-only guard.
        text = text.replace(
            STREAM_URL_SELECTION_PATCHED_LEGACY,
            STREAM_URL_SELECTION_PATCHED,
            1,
        )
        stream_url_changed = True
    else:
        text, stream_url_changed = patch_text(
            text,
            STREAM_URL_SELECTION_ORIGINAL,
            STREAM_URL_SELECTION_PATCHED,
            playbackmanager,
        )

    text, hls_set_src_changed = patch_text(
        text,
        SET_SRC_INTO_PLAYER_PATCHED,
        SET_SRC_INTO_PLAYER_ORIGINAL,
        playbackmanager,
    )
    text, hls_error_changed = patch_text(
        text,
        PLAYBACK_ERROR_PATCHED,
        PLAYBACK_ERROR_ORIGINAL,
        playbackmanager,
    )

    if (
        helper_changed
        or seek_changed
        or container_changed
        or selection_changed
        or stream_url_changed
        or hls_set_src_changed
        or hls_error_changed
    ):
        replace_files_transactionally([(playbackmanager, text)])
    return (
        helper_changed
        or seek_changed
        or container_changed
        or selection_changed
        or stream_url_changed
        or hls_set_src_changed
        or hls_error_changed
    )


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
