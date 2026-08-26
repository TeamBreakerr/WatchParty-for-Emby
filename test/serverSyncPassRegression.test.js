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

test('master PlaybackStopped retires authority without stopping participant players', () => {
    const handler = methodBody(
        'private Task HandlePlaybackStoppedAsync',
        'public void Dispose()');

    assert.match(handler, /HandleMasterDeparture/);
    assert.match(handler, /Capture\(/);
    assert.doesNotMatch(handler, /PlaystateCommand\.Stop/);
    assert.doesNotMatch(handler, /StopParticipantsAfterMasterStop/);
    assert.doesNotMatch(source, /private async Task StopParticipantsAfterMasterStop/);
});

test('same-episode master recovery keeps followers in their current players', () => {
    assert.match(source, /Master resumed current episode/);
    assert.match(source, /retaining participant players/);
    assert.doesNotMatch(source, /Followers were stopped with the old master/);
    assert.doesNotMatch(source, /restarting \{restartSessions\.Count\} participant session/);
});

test('cross-episode handoff still sends a bounded PlayNow command path', () => {
    const episodeDispatcher = methodBody(
        'private async Task PlaySeriesEpisodeForSessions',
        'private async Task RetrySeriesEpisodeUntilConfirmedAsync');

    assert.match(episodeDispatcher, /PlayCommand = PlayCommand\.PlayNow/);
    assert.match(episodeDispatcher, /TryBeginCommandAttempt/);
});
