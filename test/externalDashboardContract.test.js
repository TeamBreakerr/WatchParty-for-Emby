'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const html = fs.readFileSync(path.resolve(__dirname, '../Configuration/external.html'), 'utf8');
const script = html.match(/<script>([\s\S]*)<\/script>/)[1];

function response(status, body = {}) {
    return {
        status,
        ok: status >= 200 && status < 300,
        headers: { get() { return null; } },
        json: async () => body,
        text: async () => JSON.stringify(body)
    };
}

function createApiHarness(responses, initialStorage = {}) {
    const stored = new Map(Object.entries(initialStorage));
    const calls = [];
    const queue = responses.slice();
    const context = {
        alert() {},
        console: { log() {}, error() {}, warn() {} },
        document: {
            activeElement: null,
            addEventListener() {},
            getElementById() {
                throw new Error('The authentication modal should not open in this harness.');
            }
        },
        fetch: async (url, options = {}) => {
            calls.push({ url, options });
            if (queue.length === 0) {
                throw new Error(`Unexpected request: ${url}`);
            }
            return queue.shift();
        },
        sessionStorage: {
            getItem(key) { return stored.has(key) ? stored.get(key) : null; },
            setItem(key, value) { stored.set(key, String(value)); },
            removeItem(key) { stored.delete(key); }
        },
        localStorage: { getItem() { return null; }, setItem() {}, removeItem() {} },
        setInterval() {},
        clearTimeout,
        setTimeout,
        window: {
            addEventListener() {},
            location: { origin: 'https://watch.example.test' }
        }
    };
    context.window.window = context.window;

    vm.createContext(context);
    vm.runInContext(`${script}\n;globalThis.__apiFetch = apiFetch;globalThis.__startParty = startParty;`, context);

    return { calls, context, stored };
}

test('external Dashboard has unique IDs and only exposes implemented playback policies', () => {
    const ids = Array.from(html.matchAll(/\sid="([^"]+)"/g), match => match[1]);
    const duplicateIds = ids.filter((id, index) => ids.indexOf(id) !== index);

    assert.deepEqual(duplicateIds, []);
    assert.match(html, /<option value="Anyone">/);
    assert.match(html, /<option value="Host">/);
    assert.match(html, /<option value="Vote">/);
    assert.doesNotMatch(html, /newHostOnlySeek|newLockSeekAhead|Network Latency Compensation|HostOnly|value="Disabled"/);
    assert.doesNotMatch(html, /joinParty|join chat|chat\.html|usernameModal/);
    assert.match(html, /id="newMaxParticipants" value="50" min="2" max="100"/);
    assert.match(html, /id="newMinReady" value="1" min="1"/);
});

test('external Dashboard uses the same clear Chinese room and control terms as the embedded page', () => {
    assert.match(html, />主控用户（必选）</);
    assert.match(html, />允许加入的用户（可选）</);
    assert.match(html, />启用等候室</);
    assert.match(html, /<option value="Anyone">任何人都可播放或暂停<\/option>/);
    assert.match(html, /<option value="Host">仅主控用户可播放或暂停<\/option>/);
    assert.match(html, /<option value="Vote">由参与者投票决定<\/option>/);
    assert.match(html, />主控跳转识别阈值（秒）</);
    assert.match(html, /115\/小雅流被反复 seek/);
    assert.match(script, /单集模式：请选择季和集/);
    assert.match(script, /整部剧集房间：会按季和集数/);
});

test('external Dashboard delegates card actions safely and exposes manual waiting-room start', () => {
    assert.doesNotMatch(html, /onclick="(?:playVideo|deleteParty|startParty)\(/);
    assert.match(script, /partyGrid\.addEventListener\('click', handlePartyGridClick\)/);
    assert.match(script, /data-party-action="play"/);
    assert.match(script, /data-party-action="delete"/);
    assert.match(script, /party\.IsWaitingRoom[\s\S]*data-party-action="start"/);
    assert.match(script, /apiFetch\(`\$\{window\.location\.origin\}\/api\/parties\/start`/);
    assert.match(script, /JSON\.stringify\(\{ partyId: partyId \}\)/);
});

test('external Dashboard routes all protected management and Emby calls through apiFetch', () => {
    assert.match(script, /apiFetch\(`\$\{webServerUrl\}\/api\/parties\/create`/);
    assert.match(script, /apiFetch\(`\$\{webServerUrl\}\/api\/parties\/delete`/);
    assert.match(script, /apiFetch\(`\$\{window\.location\.origin\}\/api\/parties\/start`/);
    assert.match(script, /apiFetch\(`\$\{webServerUrl\}\/api\/emby\//);
    assert.doesNotMatch(script, /\bfetch\(`\$\{(?:webServerUrl|window\.location\.origin)\}\/api\/(?:emby\/|parties\/(?:create|delete|start))/);
});

test('apiFetch adds the session token and reusable CSRF token to POST requests', async () => {
    const harness = createApiHarness([response(200)], {
        watchPartySessionToken: 'session-one',
        watchPartyCsrfToken: 'csrf-one'
    });

    await harness.context.__apiFetch('/api/parties/create', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: '{}'
    });

    assert.equal(harness.calls.length, 1);
    assert.equal(harness.calls[0].options.headers['X-Session-Token'], 'session-one');
    assert.equal(harness.calls[0].options.headers['X-CSRF-Token'], 'csrf-one');
    assert.equal(harness.calls[0].options.headers['Content-Type'], 'application/json');
});

test('manual waiting-room start posts the selected party and refreshes the room list', async () => {
    const harness = createApiHarness([response(200)], {
        watchPartySessionToken: 'session-one',
        watchPartyCsrfToken: 'csrf-one'
    });
    vm.runInContext('globalThis.__reloadCount = 0; loadParties = () => { globalThis.__reloadCount += 1; };', harness.context);

    await harness.context.__startParty({ stopPropagation() {} }, 'party-one');

    assert.equal(harness.calls.length, 1);
    assert.equal(harness.calls[0].url, 'https://watch.example.test/api/parties/start');
    assert.equal(harness.calls[0].options.method, 'POST');
    assert.equal(harness.calls[0].options.headers['X-CSRF-Token'], 'csrf-one');
    assert.equal(harness.calls[0].options.body, JSON.stringify({ partyId: 'party-one' }));
    assert.equal(harness.context.__reloadCount, 1);
});

test('apiFetch leaves trusted-proxy SSO responses alone and never sends CSRF on GET', async () => {
    const harness = createApiHarness([response(200)]);
    vm.runInContext('ensureAdministratorLogin = async () => { throw new Error("login should not run"); };', harness.context);

    const result = await harness.context.__apiFetch('/api/emby/Users');

    assert.equal(result.status, 200);
    assert.equal(harness.calls.length, 1);
    assert.equal('X-CSRF-Token' in harness.calls[0].options.headers, false);
});

test('apiFetch clears stale credentials, signs in, and retries a 401 only once', async () => {
    const harness = createApiHarness([response(401), response(401)], {
        watchPartySessionToken: 'stale-session',
        watchPartyCsrfToken: 'stale-csrf'
    });
    vm.runInContext(`
        globalThis.__loginCount = 0;
        ensureAdministratorLogin = async () => {
            globalThis.__loginCount += 1;
            sessionStorage.setItem('watchPartySessionToken', 'fresh-session');
            sessionStorage.setItem('watchPartyCsrfToken', 'fresh-csrf');
            return true;
        };
    `, harness.context);

    const result = await harness.context.__apiFetch('/api/parties/delete', { method: 'POST' });

    assert.equal(result.status, 401);
    assert.equal(harness.context.__loginCount, 1);
    assert.equal(harness.calls.length, 2);
    assert.equal(harness.calls[0].options.headers['X-Session-Token'], 'stale-session');
    assert.equal(harness.calls[0].options.headers['X-CSRF-Token'], 'stale-csrf');
    assert.equal(harness.calls[1].options.headers['X-Session-Token'], 'fresh-session');
    assert.equal(harness.calls[1].options.headers['X-CSRF-Token'], 'fresh-csrf');
});

test('external Dashboard provides responsive and keyboard-accessible controls', () => {
    assert.match(html, /@media \(max-width: 480px\)/);
    assert.match(html, /role="combobox"[^>]+aria-controls="searchContentDropdown"[^>]+aria-expanded="false"/);
    assert.match(html, /id="searchContentDropdown"[^>]+role="listbox"/);
    assert.match(script, /event\.key === 'ArrowDown'/);
    assert.match(script, /event\.key === 'ArrowUp'/);
    assert.match(script, /event\.key === 'Escape'/);
    assert.match(script, /function updateWaitingRoomOptions\(\)/);
    assert.match(script, /function updateInactiveUserOptions\(\)/);
    assert.match(script, /userData\.csrfToken[\s\S]*sessionStorage\.setItem\(csrfTokenStorageKey, ssoCsrfToken\)/);
});
