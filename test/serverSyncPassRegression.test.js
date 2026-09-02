'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');

const source = fs.readFileSync(
    path.resolve(__dirname, '../ServerEntryPoint.cs'),
    'utf8');

test('active rooms rely on Emby native WebSocket ping instead of an application KeepAlive', () => {
    assert.doesNotMatch(source, /KeepOfficialIosParticipantConnectionsAlive|TrySendKeepAliveAsync/);
    assert.doesNotMatch(source, /"KeepAlive"/);
});

function methodBody(startMarker, endMarker) {
    const start = source.indexOf(startMarker);
    const end = source.indexOf(endMarker, start);
    assert.notEqual(start, -1, `missing start marker: ${startMarker}`);
    assert.notEqual(end, -1, `missing end marker: ${endMarker}`);
    return source.slice(start, end);
}

test('PlaybackStopped exhaustively applies the lifecycle policy without follower Stop', () => {
    const lifecycleRegion = methodBody(
        'private Task ResolveStoppedPlaybackGeneration',
        'public void Dispose()');

    assert.match(lifecycleRegion, /MasterPlaybackLifecyclePolicy\.DecidePlaybackStopped/);
    assert.match(lifecycleRegion, /PlaybackStopDisposition\.RetainForEpisodeTransition/);
    assert.match(lifecycleRegion, /ResolvePlaybackStop/);
    assert.match(lifecycleRegion, /_masterSessionLifecycles\.Execute/);
    assert.doesNotMatch(lifecycleRegion, /PlaystateCommand\.Stop/);
    assert.doesNotMatch(source, /private async Task StopParticipantsAfterMasterStop/);
});

test('natural series completion is wired through the tested authorization coordinator', () => {
    const stoppedHandler = methodBody(
        'private Task HandlePlaybackStopped',
        'public void Dispose()');
    const naturalAdvance = methodBody(
        'private Task ResolveStoppedPlaybackGenerationOrAdvanceEpisode',
        'private StoppedPlaybackLifecycleResolution ResolveStoppedPlaybackGenerationCore');
    const playbackStart = methodBody(
        'private async Task HandlePlaybackStartAsync',
        'private async void CheckAndSyncUsers');

    assert.match(stoppedHandler, /ResolveStoppedPlaybackGenerationOrAdvanceEpisode/);
    assert.match(naturalAdvance, /PlayedToCompletion = e\.PlayedToCompletion/);
    assert.match(naturalAdvance, /_naturalEpisodeAdvances\.TryBegin/);
    assert.match(naturalAdvance, /ResolveStoppedPlaybackGenerationCore/);
    assert.match(naturalAdvance, /authorization\.SessionId/);
    assert.match(naturalAdvance, /TryBeginCommandAttempt/);
    assert.match(playbackStart, /ClassifyPlaybackStart/);
    assert.match(playbackStart, /RejectDifferentSession/);
    assert.match(playbackStart, /_naturalEpisodeAdvances\.TryComplete/);
    assert.doesNotMatch(naturalAdvance, /TrySelectEpisode/);
});

test('same-episode master recovery keeps followers in their current players', () => {
    assert.match(source, /MasterPlaybackLifecyclePolicy\.DecideMasterPlaybackStarted/);
    assert.match(source, /Master authority resumed for item/);
    assert.match(source, /retaining participant players/);
    assert.doesNotMatch(source, /Followers were stopped with the old master/);
    assert.doesNotMatch(source, /restarting \{restartSessions\.Count\} participant session/);
});

test('same-episode master recovery reconciles active followers without PlayNow', () => {
    const recovery = methodBody(
        'private async Task HandlePlaybackStartAsync',
        'private async void CheckAndSyncUsers');

    assert.match(recovery, /MasterPlaybackStartDisposition\.ReestablishAuthority/);
    assert.match(recovery, /HandleMasterPause/);
    assert.match(recovery, /HandleMasterResume/);
});

test('a retained follower Stop forces its next accepted Start through reconciliation', () => {
    const dormantStop = methodBody(
        'private void MarkParticipantDormant',
        'private Task ResolveStoppedPlaybackGeneration');
    const playbackStart = methodBody(
        'private async Task HandlePlaybackStartAsync',
        'private async void CheckAndSyncUsers');

    assert.match(dormantStop, /ClearSessionEpisodeMarkers/);
    assert.match(playbackStart, /resumedFromStart/);
    assert.match(playbackStart, /ReconcileParticipantAfterReconnect/);
});

test('one Emby session cannot remain controllable from two rooms', () => {
    const registration = methodBody(
        'private PartyParticipant GetOrCreateParticipant(',
        'private void ClearSessionEpisodeMarkers');

    assert.match(registration, /TryClaimSession/);
    assert.match(registration, /displacedMemberships/);
    assert.match(registration, /CompleteParticipantRemoval/);
    assert.match(registration, /FinalizeDisplacedMasterMemberships/);
    assert.doesNotMatch(registration, /TryUpsertSession/);
});

test('cross-episode handoff still sends a bounded PlayNow command path', () => {
    const requestFactory = methodBody(
        'private PlayRequest CreateEpisodePlayRequest',
        'private async Task<int> PlaySeriesEpisodeForSessions');
    const episodeDispatcher = methodBody(
        'private async Task<int> PlaySeriesEpisodeForSessions',
        'private async Task RetrySeriesEpisodeUntilConfirmedAsync');

    assert.match(requestFactory, /PlayCommand = PlayCommand\.PlayNow/);
    assert.match(requestFactory, /PlaybackMediaSourceSelector\.Resolve/);
    assert.match(episodeDispatcher, /CreateEpisodePlayRequest/);
    assert.match(episodeDispatcher, /TryBeginCommandAttempt/);
});

test('configuration episode switching uses the generation-safe series PlayNow path', () => {
    const episodeSwitch = methodBody(
        'public async Task<PartyEpisodeSelectionResult> SelectPartyEpisodeNowAsync',
        'private bool HasPlaybackControlConnection');

    assert.match(episodeSwitch, /EnterSeriesTransitionAsync/);
    assert.match(episodeSwitch, /PartyEpisodeSelectionCoordinator\.TryCommit/);
    assert.match(episodeSwitch, /hasDispatchableMaster/);
    assert.match(episodeSwitch, /MasterCanReceivePlayback\s*=\s*hasDispatchableMaster/);
    assert.match(
        episodeSwitch,
        /if \(shouldDispatch\)[\s\S]*?ResetSeriesEpisodeSyncState/);
    assert.match(episodeSwitch, /PlaySeriesEpisodeForSessions/);
    assert.match(episodeSwitch, /CommandTargetCount = commandSentCount/);
    assert.match(source, /private PlayRequest CreateEpisodePlayRequest/);
});

test('series transition targets require a live command transport', () => {
    const targetSelection = methodBody(
        'private List<SessionInfo> GetSeriesTransitionSessions',
        'private IReadOnlyCollection<SeriesPlaybackHandoffSession>');

    assert.match(targetSelection, /PartySessionLivenessPolicy\.IsOnline/);
    assert.match(targetSelection, /SupportsRemoteControlledPlayback/);
    assert.match(targetSelection, /HasPlaybackControlConnection/);
});

test('manual series launch validates the selected episode media source', () => {
    const manualLaunch = methodBody(
        'public async Task<PartyManualSynchronizationResult> SynchronizePartyNowAsync',
        'private bool HasPlaybackControlConnection');

    assert.match(manualLaunch, /targetEpisode == null/);
    assert.match(manualLaunch, /CreateEpisodePlayRequest/);
    assert.doesNotMatch(
        manualLaunch,
        /MediaSourceId = targetEpisode\?\.MediaSourceId \?\? party\.MediaSourceId/);
});

test('manual single-item launch validates the selected media source', () => {
    const manualLaunch = methodBody(
        'public async Task<PartyManualSynchronizationResult> SynchronizePartyNowAsync',
        'private bool HasPlaybackControlConnection');
    const requestFactory = methodBody(
        'private PlayRequest CreateSingleItemPlayRequest',
        'private PlayRequest CreateEpisodePlayRequest');
    const sourceResolver = methodBody(
        'private PlayRequest CreateValidatedPlayRequest',
        'private async Task<int> PlaySeriesEpisodeForSessions');

    assert.match(manualLaunch, /CreateSingleItemPlayRequest\(/);
    assert.match(sourceResolver, /PlaybackMediaSourceSelector\.Resolve/);
    assert.match(requestFactory, /party\?\.MediaSourceId/);
    assert.match(sourceResolver, /MediaSourceId = mediaSourceId/);
});

test('configuration maintenance releases its lock before entering a party lifecycle boundary', () => {
    assert.doesNotMatch(
        source,
        /lock \(_plugin\.ConfigurationSyncRoot\)\s*\{\s*CleanupRemovedPartiesCore\(\)/);
    assert.doesNotMatch(
        source,
        /lock \(_plugin\.ConfigurationSyncRoot\)\s*\{\s*CleanupIneligibleParticipantsCore\(\)/);

    const removedPartyCleanup = methodBody(
        'private void CleanupRemovedParties()',
        'private void ClearPartyRuntimeState');
    assert.match(
        removedPartyCleanup,
        /Never enter a party lifecycle boundary while holding the\s*\/\/ configuration lock/);
    assert.match(removedPartyCleanup, /ClearPartyRuntimeState\(removedId\)/);
});

test('a stale master cannot replace the current authority pause confirmation', () => {
    const progressHandler = methodBody(
        'private async Task HandlePlaybackProgressAsync',
        'private void ScheduleMasterPauseStateCommit');
    assert.match(
        progressHandler,
        /if \(deferMasterPauseTransition\)[\s\S]*?TryExecuteCurrentMasterPlayback\([\s\S]*?ScheduleMasterPauseStateCommit/);
    assert.match(progressHandler, /if \(!scheduledForCurrentAuthority\)[\s\S]*?return;/);
});
