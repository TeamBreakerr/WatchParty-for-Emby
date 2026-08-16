'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const allEpisodes = [
    { Id: 's2e1', Name: 'Season Two', SeasonId: 'season-2', ParentIndexNumber: 2, IndexNumber: 1 },
    { Id: 's1e2', Name: 'Second', SeasonId: 'season-1', ParentIndexNumber: 1, IndexNumber: 2 },
    { Id: 's1e1', Name: 'First', SeasonId: 'season-1', ParentIndexNumber: 1, IndexNumber: 1 }
];

function element(overrides = {}) {
    const attributes = new Map();
    return {
        value: '',
        checked: false,
        dataset: {},
        selectedOptions: [],
        selectedIndex: -1,
        style: {},
        textContent: '',
        hidden: false,
        classList: { add() {}, remove() {}, toggle() {}, contains() { return false; } },
        addEventListener() {},
        removeEventListener() {},
        setAttribute(name, value) { attributes.set(name, String(value)); },
        removeAttribute(name) { attributes.delete(name); },
        focus() {},
        ...overrides
    };
}

function createHarness({
    isSeriesParty,
    seasonId = '',
    episodeId = '',
    allowedUserIds = [],
    pauseControl = 'Anyone',
    maxParticipants = '10',
    minReadyCount = '2'
}) {
    const fields = new Map();
    const add = (id, overrides) => fields.set(id, element(overrides));
    const alerts = [];
    const submittedParties = [];

    add('serverUrl', { value: 'http://emby:8096' });
    add('newSourceLibrary', { value: 'source-library' });
    add('newContentItem', { value: 'series-1', dataset: { type: 'Series', name: 'Example Series' } });
    add('newEpisode', { value: episodeId, dataset: { name: episodeId ? 'Second' : '' } });
    add('newSeason', { value: seasonId });
    add('newIsSeriesParty', { checked: isSeriesParty });
    add('newMaxParticipants', { value: maxParticipants });
    add('newPartyPassword', { value: '' });
    add('newMasterUser', { value: 'master-user' });
    add('newAllowedUsers', { selectedOptions: allowedUserIds.map(value => ({ value })) });
    add('newIsActive', { checked: true });
    add('newWaitingRoom', { checked: true });
    add('newAutoStart', { checked: true });
    add('newMinReady', { value: minReadyCount });
    add('newPauseControl', { value: pauseControl });
    add('newSyncTolerance', { value: '10' });
    add('newMaxBuffer', { value: '30' });
    add('newAutoKick', { checked: true });
    add('newInactiveTimeout', { value: '15' });

    const document = {
        getElementById(id) {
            if (!fields.has(id)) {
                fields.set(id, element());
            }
            return fields.get(id);
        },
        createElement() {
            return element({ appendChild() {}, cloneNode() { return element(); } });
        },
        addEventListener() {},
        activeElement: null
    };

    const fetch = async url => {
        if (url.includes('/api/emby/Shows/series-1/Episodes?')) {
            return { ok: true, status: 200, headers: { get() { return null; } }, json: async () => ({ Items: allEpisodes }) };
        }
        if (url.endsWith('/api/parties/create')) {
            return {
                ok: true,
                status: 200,
                headers: { get() { return null; } },
                json: async () => ({ success: true })
            };
        }
        throw new Error(`Unexpected request: ${url}`);
    };

    const context = {
        alert: message => alerts.push(message),
        console: { log() {}, error() {}, warn() {} },
        document,
        fetch: async (url, options) => {
            if (url.endsWith('/api/parties/create')) {
                submittedParties.push(JSON.parse(options.body));
                return { ok: true, status: 200, headers: { get() { return null; } }, json: async () => ({ success: true }) };
            }
            return fetch(url, options);
        },
        sessionStorage: { getItem() { return null; }, setItem() {}, removeItem() {} },
        localStorage: { getItem() { return null; }, setItem() {}, removeItem() {} },
        setInterval() {},
        setTimeout,
        window: {
            addEventListener() {},
            location: { origin: 'http://localhost:8097' }
        }
    };
    context.window.window = context.window;

    const html = fs.readFileSync(path.resolve(__dirname, '../Configuration/external.html'), 'utf8');
    const script = html.match(/<script>([\s\S]*)<\/script>/)[1];
    vm.createContext(context);
    vm.runInContext(`${script}\n;globalThis.__createParty = createParty;`, context);

    return {
        alerts,
        fields,
        submittedParties,
        submit: () => context.__createParty()
    };
}

test('external Dashboard Series Party skips single-episode validation and builds the full queue', async () => {
    const harness = createHarness({ isSeriesParty: true });
    await harness.submit();

    assert.equal(harness.submittedParties.length, 1, harness.alerts.join('\n'));
    const party = harness.submittedParties[0];
    assert.equal(party.isSeriesParty, true);
    assert.equal(party.itemId, 's1e1');
    assert.equal(party.currentEpisodeId, 's1e1');
    assert.equal(party.currentEpisodeIndex, 0);
    assert.deepEqual(Array.from(party.episodeQueue, episode => episode.ItemId), ['s1e1', 's1e2', 's2e1']);
    assert.equal('targetLibraryId' in party, false);
    assert.equal('collectionName' in party, false);
    assert.doesNotMatch(harness.alerts.join('\n'), /单集模式下/);
});

test('external Dashboard Series Party resolves an optional season starting point', async () => {
    const harness = createHarness({ isSeriesParty: true, seasonId: 'season-2' });
    await harness.submit();

    const party = harness.submittedParties[0];
    assert.equal(party.itemId, 's2e1');
    assert.equal(party.currentEpisodeIndex, 2);
});

test('external Dashboard single-episode mode still requires an episode', async () => {
    const harness = createHarness({ isSeriesParty: false });
    await harness.submit();

    assert.equal(harness.submittedParties.length, 0);
    assert.match(harness.alerts.join('\n'), /单集模式下，请选择要观看的剧集/);
});

test('external Dashboard adds the master to a non-empty whitelist and uses the shared pause enum', async () => {
    const harness = createHarness({
        isSeriesParty: true,
        allowedUserIds: ['viewer-user'],
        pauseControl: 'Vote'
    });
    await harness.submit();

    const party = harness.submittedParties[0];
    assert.deepEqual(Array.from(party.allowedUserIds), ['master-user', 'viewer-user']);
    assert.equal(party.pauseControl, 'Vote');
    assert.equal('hostOnlySeek' in party, false);
    assert.equal('lockSeekAhead' in party, false);
    assert.equal('enableNetworkLatencyCompensation' in party, false);
});

test('external Dashboard rejects a ready threshold above room capacity', async () => {
    const harness = createHarness({
        isSeriesParty: true,
        maxParticipants: '3',
        minReadyCount: '4'
    });

    await harness.submit();

    assert.equal(harness.submittedParties.length, 0);
    assert.match(harness.alerts.join('\n'), /最低就绪人数必须在 1 到 3 之间/);
});
