'use strict';

const assert = require('node:assert/strict');
const path = require('node:path');
const test = require('node:test');

const allEpisodes = [
    { Id: 's2e1', Name: 'Season Two', SeasonId: 'season-2', ParentIndexNumber: 2, IndexNumber: 1 },
    { Id: 's1e2', Name: 'Second', SeasonId: 'season-1', ParentIndexNumber: 1, IndexNumber: 2 },
    { Id: 's1e1', Name: 'First', SeasonId: 'season-1', ParentIndexNumber: 1, IndexNumber: 1 }
];

function element(overrides = {}) {
    return {
        value: '',
        checked: false,
        disabled: false,
        dataset: {},
        selectedOptions: [],
        selectedIndex: -1,
        style: {},
        textContent: '',
        classList: { remove() {} },
        ...overrides
    };
}

function loadController(toasts, updatedConfigurations) {
    global.ApiClient = {
        getPluginConfiguration: async () => ({ WatchParties: [] }),
        updatePluginConfiguration: async (_pluginId, configuration) => {
            updatedConfigurations.push(configuration);
            return {};
        },
        getCurrentUserId: () => 'admin-user',
        getEpisodes: async () => ({ Items: allEpisodes })
    };
    global.Dashboard = { processPluginConfigurationUpdateResult() {} };
    global.document = { createElement: () => ({ set textContent(_value) {}, get innerHTML() { return ''; } }) };

    let Controller;
    global.define = (_dependencies, factory) => {
        class BaseView {}
        Controller = factory(
            BaseView,
            { show() {}, hide() {} },
            payload => toasts.push(typeof payload === 'string' ? payload : payload.text),
            {},
            {},
            {},
            {}
        );
    };

    const modulePath = path.resolve(__dirname, '../Configuration/configPage.js');
    delete require.cache[modulePath];
    require(modulePath);
    return Object.create(Controller.prototype);
}

function createView() {
    const fields = new Map();
    const add = (selector, overrides) => fields.set(selector, element(overrides));

    add('#selectedLibraryId', { value: 'source-library' });
    add('#selectedItemId', { value: 'series-1', dataset: { type: 'Series', name: 'Example Series' } });
    add('#searchContent', { value: 'Example Series [TV Show]' });
    add('#selectedSeasonId', { value: '' });
    add('#selectedEpisodeId', { value: '' });
    add('#searchEpisode', { value: '' });
    add('#isSeriesParty', { checked: true });
    add('#maxParticipants', { value: '50' });
    add('#syncToleranceSeconds', { value: '10' });
    add('#allowedUsers', { selectedOptions: [] });
    add('#masterUser', { value: 'master-user' });
    add('#partyPassword', { value: '' });
    add('#isPartyActive', { checked: false });
    add('#isWaitingRoom', { checked: false });
    add('#autoStartWhenReady', { checked: false });
    add('#minReadyCount', { value: '1' });
    add('#pauseControl', { value: 'Anyone' });
    add('#hostOnlySeek', { checked: true });
    add('#lockSeekAhead', { checked: true });
    add('#maxBufferThresholdSeconds', { value: '30' });
    add('#autoKickInactive', { checked: false });
    add('#inactiveTimeoutMinutes', { value: '15' });
    add('#enableNetworkLatencyCompensation', { checked: false });
    add('#networkLatencyMeasurementIntervalSeconds', { value: '30' });
    add('#autoAdjustForLatency', { checked: false });
    add('#maxLatencyCompensationMs', { value: '5000' });
    add('#seriesContainer');
    add('#episodeContainer');
    add('#seriesPartyContainer');
    add('#seriesPartyModeHint');
    add('#seasonFieldDescription');
    add('#episodeFieldLabel');
    add('#episodeFieldDescription');
    add('#searchSeason');
    add('#searchSeasonDropdown');
    add('#searchEpisodeDropdown');

    return {
        querySelector(selector) {
            if (!fields.has(selector)) {
                fields.set(selector, element());
            }
            return fields.get(selector);
        }
    };
}

async function submit(view) {
    const toasts = [];
    const updatedConfigurations = [];
    const controller = loadController(toasts, updatedConfigurations);
    controller.saveData(view);
    await new Promise(resolve => setTimeout(resolve, 20));
    return { controller, toasts, updatedConfigurations };
}

test('Series Party without season or episode starts from the first regular episode', async () => {
    const result = await submit(createView());

    assert.doesNotMatch(result.toasts.join('\n'), /单集电视剧房间必须选择一集/);
    assert.equal(result.updatedConfigurations.length, 1, result.toasts.join('\n'));
    const party = result.updatedConfigurations[0].WatchParties[0];
    assert.equal(party.IsSeriesParty, true);
    assert.deepEqual(party.EpisodeQueue.map(episode => episode.ItemId), ['s1e1', 's1e2', 's2e1']);
    assert.equal(party.CurrentEpisodeId, 's1e1');
    assert.equal(party.CurrentEpisodeIndex, 0);
    assert.equal(party.ItemId, 's1e1');
    assert.equal('TargetLibraryId' in party, false);
    assert.equal('TargetLibraryPath' in party, false);
    assert.equal('CollectionName' in party, false);
});

test('Series Party with only a season starts from the first episode in that season', async () => {
    const view = createView();
    view.querySelector('#selectedSeasonId').value = 'season-2';

    const result = await submit(view);

    assert.equal(result.updatedConfigurations.length, 1, result.toasts.join('\n'));
    const party = result.updatedConfigurations[0].WatchParties[0];
    assert.equal(party.CurrentEpisodeId, 's2e1');
    assert.equal(party.CurrentEpisodeIndex, 2);
    assert.equal(party.ItemId, 's2e1');
});

test('Series Party with a selected episode starts from that episode', async () => {
    const view = createView();
    view.querySelector('#selectedSeasonId').value = 'season-1';
    view.querySelector('#selectedEpisodeId').value = 's1e2';
    view.querySelector('#searchEpisode').value = 'S1E2 - Second';

    const result = await submit(view);

    assert.equal(result.updatedConfigurations.length, 1, result.toasts.join('\n'));
    const party = result.updatedConfigurations[0].WatchParties[0];
    assert.equal(party.CurrentEpisodeId, 's1e2');
    assert.equal(party.CurrentEpisodeIndex, 1);
    assert.equal(party.ItemId, 's1e2');
});

test('single-episode Party still requires an episode', async () => {
    const view = createView();
    view.querySelector('#isSeriesParty').checked = false;

    const result = await submit(view);

    assert.match(result.toasts.join('\n'), /单集电视剧房间必须选择一集/);
    assert.equal(result.updatedConfigurations.length, 0);
});

test('Movie Party remains a single-item Party', async () => {
    const view = createView();
    view.querySelector('#selectedItemId').value = 'movie-1';
    view.querySelector('#selectedItemId').dataset = { type: 'Movie', name: 'Example Movie' };
    view.querySelector('#searchContent').value = 'Example Movie';
    view.querySelector('#isSeriesParty').checked = false;

    const result = await submit(view);

    assert.equal(result.updatedConfigurations.length, 1, result.toasts.join('\n'));
    const party = result.updatedConfigurations[0].WatchParties[0];
    assert.equal(party.IsSeriesParty, false);
    assert.equal(party.ItemId, 'movie-1');
    assert.deepEqual(party.EpisodeQueue, []);
    assert.equal('TargetLibraryId' in party, false);
});
