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
