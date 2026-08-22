'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');

const source = fs.readFileSync(
    path.resolve(__dirname, '../ServerEntryPoint.cs'),
    'utf8');

test('active rooms keep iOS control connections alive while the master is temporarily absent', () => {
    const methodStart = source.indexOf('private async void CheckAndSyncUsers');
    const methodEnd = source.indexOf(
        'private async Task KeepOfficialIosParticipantConnectionsAlive',
        methodStart);
    const method = source.slice(methodStart, methodEnd);
    const keepAliveCall = method.indexOf(
        'await KeepOfficialIosParticipantConnectionsAlive(party);');
    const playbackCalibrationGate = method.indexOf(
        'if (party.IsWaitingRoom || !HasActiveMasterSession(party))');

    assert.notEqual(methodStart, -1);
    assert.notEqual(methodEnd, -1);
    assert.notEqual(keepAliveCall, -1);
    assert.notEqual(playbackCalibrationGate, -1);
    assert.ok(
        keepAliveCall < playbackCalibrationGate,
        'iOS WebSocket keep-alive must run before playback calibration is gated on an active master');
});
