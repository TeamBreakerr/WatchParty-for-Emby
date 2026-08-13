'use strict';

const assert = require('node:assert/strict');
const path = require('node:path');
const test = require('node:test');

function element(overrides = {}) {
    return {
        value: '',
        checked: false,
        disabled: false,
        dataset: {},
        selectedOptions: [],
        selectedIndex: -1,
        style: {},
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
        getEpisodes: async () => ({
            Items: [
                { Id: 'episode-2', Name: 'Second', SeasonId: 'season-1', ParentIndexNumber: 1, IndexNumber: 2 },
                { Id: 'episode-1', Name: 'First', SeasonId: 'season-1', ParentIndexNumber: 1, IndexNumber: 1 }
            ]
        })
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

function createSeriesPartyView() {
    const fields = new Map();
    const add = (selector, overrides) => fields.set(selector, element(overrides));

    add('#selectedLibraryId', { value: 'source-library' });
    add('#selectedItemId', { value: 'series-1', dataset: { type: 'Series', name: 'Example Series' } });
    add('#searchContent', { value: 'Example Series [TV Show]' });
    add('#selectedSeasonId', { value: '' });
    add('#selectedEpisodeId', { value: '' });
    add('#searchEpisode', { value: '' });
    add('#isSeriesParty', { checked: true });
    add('#libraryName', {
        value: 'watch-party-library',
        selectedOptions: [{ dataset: { name: 'Watch Party', path: '/config/watchparty' } }]
    });
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

test('series party can use the first queued episode when no episode is selected', async () => {
    const toasts = [];
    const updatedConfigurations = [];
    const controller = loadController(toasts, updatedConfigurations);
    const view = createSeriesPartyView();

    controller.saveData(view);
    await new Promise(resolve => setTimeout(resolve, 20));

    assert.doesNotMatch(toasts.join('\n'), /Please select an episode for TV shows/);
    assert.equal(updatedConfigurations.length, 1, toasts.join('\n'));
    const createdParty = updatedConfigurations[0].WatchParties[0];
    assert.equal(createdParty.IsSeriesParty, true);
    assert.equal(createdParty.CurrentEpisodeId, 'episode-1');
    assert.equal(createdParty.CurrentEpisodeIndex, 0);
    assert.equal(createdParty.ItemId, 'episode-1');
});

test('single-episode party still requires an episode selection', async () => {
    const toasts = [];
    const updatedConfigurations = [];
    const controller = loadController(toasts, updatedConfigurations);
    const view = createSeriesPartyView();
    view.querySelector('#isSeriesParty').checked = false;

    controller.saveData(view);
    await new Promise(resolve => setTimeout(resolve, 20));

    assert.match(toasts.join('\n'), /Please select an episode for TV shows/);
    assert.equal(updatedConfigurations.length, 0);
});

test('selected episode overrides the default Series Party starting episode', async () => {
    const toasts = [];
    const updatedConfigurations = [];
    const controller = loadController(toasts, updatedConfigurations);
    const view = createSeriesPartyView();
    view.querySelector('#selectedEpisodeId').value = 'episode-2';
    view.querySelector('#searchEpisode').value = 'S1E2 - Second';
    view.querySelector('#selectedSeasonId').value = 'season-1';

    controller.saveData(view);
    await new Promise(resolve => setTimeout(resolve, 20));

    assert.equal(updatedConfigurations.length, 1, toasts.join('\n'));
    const createdParty = updatedConfigurations[0].WatchParties[0];
    assert.equal(createdParty.CurrentEpisodeId, 'episode-2');
    assert.equal(createdParty.CurrentEpisodeIndex, 1);
    assert.equal(createdParty.ItemId, 'episode-2');
});
