'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');

const allEpisodes = [
    { Id: 's2e1', Name: 'Season Two', SeasonId: 'season-2', ParentIndexNumber: 2, IndexNumber: 1 },
    { Id: 's1e2', Name: 'Second', SeasonId: 'season-1', ParentIndexNumber: 1, IndexNumber: 2 },
    { Id: 's1e1', Name: 'First', SeasonId: 'season-1', ParentIndexNumber: 1, IndexNumber: 1 }
];

function element(overrides = {}) {
    const attributes = new Map();
    const classes = new Set();
    const listeners = new Map();
    const result = {
        value: '',
        checked: false,
        disabled: false,
        dataset: {},
        options: [],
        children: [],
        selectedIndex: -1,
        style: {},
        textContent: '',
        classList: {
            add(name) { classes.add(name); },
            remove(name) { classes.delete(name); },
            contains(name) { return classes.has(name); },
            toggle(name, enabled) {
                if (enabled) classes.add(name);
                else classes.delete(name);
            }
        },
        addEventListener(name, listener) { listeners.set(name, listener); },
        dispatch(name, event = {}) {
            const listener = listeners.get(name);
            if (listener) listener(event);
        },
        click() { this.dispatch('click', {}); },
        appendChild(child) {
            this.options.push(child);
            this.children.push(child);
            return child;
        },
        setAttribute(name, value) { attributes.set(name, String(value)); },
        getAttribute(name) { return attributes.get(name) ?? null; },
        removeAttribute(name) { attributes.delete(name); },
        contains() { return false; },
        querySelectorAll(selector) {
            if (selector === '[role="option"]') {
                return this.children.filter(child => child.getAttribute('role') === 'option');
            }
            return [];
        }
    };

    let innerHtml = '';
    Object.defineProperty(result, 'innerHTML', {
        configurable: true,
        get() { return innerHtml; },
        set(value) {
            innerHtml = String(value);
            if (value === '') {
                this.options = [];
                this.children = [];
            }
        }
    });
    Object.defineProperty(result, 'selectedOptions', {
        configurable: true,
        get() { return this.options.filter(option => option.selected); }
    });
    Object.assign(result, overrides);
    return result;
}

function loadController(toasts, updatedConfigurations, configuration = { WatchParties: [] }) {
    global.ApiClient = {
        getPluginConfiguration: async () => configuration,
        updatePluginConfiguration: async (_pluginId, configuration) => {
            updatedConfigurations.push(configuration);
            return {};
        },
        getJSON: async () => ({ Items: [] }),
        getUrl: value => value,
        getUsers: async () => [],
        getCurrentUserId: () => 'admin-user',
        getEpisodes: async () => ({ Items: allEpisodes })
    };
    global.Dashboard = { processPluginConfigurationUpdateResult() {} };
    global.document = {
        createElement: tagName => element({ tagName: String(tagName).toUpperCase() }),
        addEventListener() {}
    };

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
    add('#allowedUsers', { options: [] });
    add('#masterUser', { value: 'master-user' });
    add('#partyPassword', { value: '' });
    add('#isPartyActive', { checked: false });
    add('#isWaitingRoom', { checked: false });
    add('#autoStartWhenReady', { checked: false });
    add('#minReadyCount', { value: '1' });
    add('#pauseControl', { value: 'Anyone' });
    add('#maxBufferThresholdSeconds', { value: '30' });
    add('#autoKickInactive', { checked: false });
    add('#inactiveTimeoutMinutes', { value: '15' });
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
        },
        querySelectorAll() { return []; }
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

test('clearing the library removes stale selection state and blocks room creation', async () => {
    const view = createView();
    const toasts = [];
    const updatedConfigurations = [];
    const controller = loadController(toasts, updatedConfigurations);
    controller.currentLibraryId = 'old-library';
    controller.currentSeriesId = 'old-series';
    controller.currentSeasonId = 'old-season';
    view.querySelector('#selectedSeasonId').value = 'old-season';
    view.querySelector('#selectedEpisodeId').value = 'old-episode';

    controller.onLibraryChange(view, '');

    assert.equal(controller.currentLibraryId, null);
    assert.equal(controller.currentSeriesId, null);
    assert.equal(controller.currentSeasonId, null);
    assert.equal(view.querySelector('#selectedItemId').value, '');
    assert.equal(view.querySelector('#selectedSeasonId').value, '');
    assert.equal(view.querySelector('#selectedEpisodeId').value, '');

    view.querySelector('#selectedLibraryId').value = '';
    view.querySelector('#selectedItemId').value = 'stale-item';
    controller.saveData(view);
    await new Promise(resolve => setTimeout(resolve, 10));

    assert.match(toasts.join('\n'), /请选择内容媒体库/);
    assert.equal(updatedConfigurations.length, 0);
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

test('restricted Party automatically includes its master and records the same host', async () => {
    const view = createView();
    view.querySelector('#allowedUsers').options = [
        element({ value: 'viewer-user', selected: true }),
        element({ value: 'master-user', selected: false })
    ];

    const result = await submit(view);

    assert.equal(result.updatedConfigurations.length, 1, result.toasts.join('\n'));
    const party = result.updatedConfigurations[0].WatchParties[0];
    assert.deepEqual(party.AllowedUserIds, ['viewer-user', 'master-user']);
    assert.equal(party.MasterUserId, 'master-user');
    assert.equal(party.HostUserId, 'master-user');
});

test('an unrestricted Party keeps an empty whitelist', async () => {
    const result = await submit(createView());
    const party = result.updatedConfigurations[0].WatchParties[0];

    assert.deepEqual(party.AllowedUserIds, []);
});

test('new Party payload only submits implemented controls and normalizes pause permission', async () => {
    const view = createView();
    view.querySelector('#pauseControl').value = 'HostOnly';

    const result = await submit(view);
    const party = result.updatedConfigurations[0].WatchParties[0];

    assert.equal(party.PauseControl, 'Host');
    assert.equal('HostOnlySeek' in party, false);
    assert.equal('LockSeekAhead' in party, false);
    assert.equal('EnableNetworkLatencyCompensation' in party, false);
    assert.equal('NetworkLatencyMeasurementIntervalSeconds' in party, false);
    assert.equal('AutoAdjustForLatency' in party, false);
    assert.equal('MaxLatencyCompensationMs' in party, false);
});

test('waiting-room auto start is disabled when the waiting room is off', async () => {
    const view = createView();
    view.querySelector('#isWaitingRoom').checked = false;
    view.querySelector('#autoStartWhenReady').checked = true;

    const result = await submit(view);
    const party = result.updatedConfigurations[0].WatchParties[0];

    assert.equal(party.IsWaitingRoom, false);
    assert.equal(party.AutoStartWhenReady, false);
});

test('successful creation completely clears content, identity, password, and whitelist selections', async () => {
    const view = createView();
    const selectedViewer = element({ value: 'viewer-user', selected: true });
    view.querySelector('#allowedUsers').options = [selectedViewer];
    view.querySelector('#partyPassword').value = 'secret';

    const result = await submit(view);

    const deadline = Date.now() + 3000;
    while (result.updatedConfigurations.length === 0 && Date.now() < deadline) {
        await new Promise(resolve => setTimeout(resolve, 10));
    }

    assert.equal(result.updatedConfigurations.length, 1, result.toasts.join('\n'));
    assert.equal(view.querySelector('#selectedItemId').value, '');
    assert.equal(view.querySelector('#selectedItemId').dataset.type, '');
    assert.equal(view.querySelector('#searchContent').value, '');
    assert.equal(view.querySelector('#masterUser').value, '');
    assert.equal(view.querySelector('#partyPassword').value, '');
    assert.equal(selectedViewer.selected, false);
    assert.equal(view.querySelector('#selectedItemId').innerHTML, '');
});

test('global settings preserve legitimate zero values', async () => {
    const view = createView();
    view.querySelector('#syncOffsetMilliseconds').value = '0';
    view.querySelector('#rateLimitRequestsPerMinute').value = '0';
    view.querySelector('#hstsMaxAge').value = '0';
    view.querySelector('#enableHttps').checked = true;
    view.querySelector('#enableHsts').checked = true;
    view.querySelector('#useReverseProxy').checked = false;

    const toasts = [];
    const updatedConfigurations = [];
    const controller = loadController(toasts, updatedConfigurations);
    controller.checkWebServerStatus = () => {};

    const nativeSetTimeout = global.setTimeout;
    global.setTimeout = (callback, delay) => {
        if (delay === 2000) {
            callback();
            return 0;
        }
        return nativeSetTimeout(callback, delay);
    };

    try {
        controller.saveGlobalSettings(view);
        await new Promise(resolve => nativeSetTimeout(resolve, 20));
    } finally {
        global.setTimeout = nativeSetTimeout;
    }

    assert.equal(updatedConfigurations.length, 1, toasts.join('\n'));
    assert.equal(updatedConfigurations[0].SyncOffsetMilliseconds, 0);
    assert.equal(updatedConfigurations[0].RateLimitRequestsPerMinute, 0);
    assert.equal(updatedConfigurations[0].HstsMaxAge, 0);
});

test('new browser-side password hashes use the server PBKDF2 format', async () => {
    const controller = loadController([], []);

    const hash = await controller.hashPassword('strong-password');

    assert.equal(Buffer.from(hash, 'base64').length, 48);
});

test('loading global settings preserves legitimate zero values', async () => {
    const configuration = {
        WatchParties: [],
        SyncOffsetMilliseconds: 0,
        RateLimitRequestsPerMinute: 0,
        HstsMaxAge: 0
    };
    const view = createView();
    const controller = loadController([], [], configuration);
    controller.checkWebServerStatus = () => {};

    controller.loadData(view);
    await new Promise(resolve => setTimeout(resolve, 20));

    assert.equal(view.querySelector('#syncOffsetMilliseconds').value, 0);
    assert.equal(view.querySelector('#rateLimitRequestsPerMinute').value, 0);
    assert.equal(view.querySelector('#hstsMaxAge').value, 0);
});

test('dashboard URL follows direct HTTPS and avoids guessing reverse-proxy routes', () => {
    const controller = loadController([], []);

    assert.equal(controller.getDashboardUrl({
        EnableHttps: true,
        UseReverseProxy: false,
        ExternalWebServerPort: 9443,
        EmbyServerUrl: 'http://192.0.2.50:8096'
    }), 'https://192.0.2.50:9443/');
    assert.equal(controller.getDashboardUrl({
        EnableHttps: true,
        UseReverseProxy: true,
        ExternalWebServerPort: 9443
    }), null);
});

test('autocomplete supports ARIA state and Arrow, Enter, and Escape keyboard control', () => {
    const view = createView();
    const input = view.querySelector('#searchContent');
    const hidden = view.querySelector('#selectedItemId');
    const dropdown = view.querySelector('#searchContentDropdown');
    const controller = loadController([], []);
    let selectedItem = null;
    input.value = '';

    controller.searchContent_items = [
        { id: 'movie-1', text: 'Movie One', type: 'Movie', name: 'Movie One' },
        { id: 'movie-2', text: 'Movie Two', type: 'Movie', name: 'Movie Two' }
    ];
    controller.searchContent_metadata = { hasMore: false };
    controller.setupAutocomplete(
        view,
        'searchContent',
        'selectedItemId',
        'searchContentDropdown',
        item => { selectedItem = item; },
        null
    );

    input.dispatch('focus');
    assert.equal(input.getAttribute('aria-expanded'), 'true');
    assert.equal(dropdown.querySelectorAll('[role="option"]').length, 2);

    const keyboardEvent = key => ({ key, preventDefault() {} });
    input.dispatch('keydown', keyboardEvent('ArrowDown'));
    assert.equal(input.getAttribute('aria-activedescendant'), 'searchContentDropdown-option-0');
    input.dispatch('keydown', keyboardEvent('Enter'));
    assert.equal(hidden.value, 'movie-1');
    assert.equal(selectedItem.id, 'movie-1');
    assert.equal(input.getAttribute('aria-expanded'), 'false');

    input.dispatch('focus');
    input.dispatch('keydown', keyboardEvent('Escape'));
    assert.equal(input.getAttribute('aria-expanded'), 'false');
});

test('dependent room and security fields are progressively disclosed', () => {
    const view = createView();
    const controller = loadController([], []);

    view.querySelector('#isWaitingRoom').checked = false;
    view.querySelector('#autoStartWhenReady').checked = true;
    view.querySelector('#autoKickInactive').checked = false;
    view.querySelector('#useReverseProxy').checked = false;
    view.querySelector('#enableHttps').checked = false;
    view.querySelector('#enableSecurityHeaders').checked = false;
    view.querySelector('#enableAccountLockout').checked = false;
    controller.updateDependentFields(view);

    assert.equal(view.querySelector('#autoStartWhenReadyContainer').style.display, 'none');
    assert.equal(view.querySelector('#minReadyCountContainer').style.display, 'none');
    assert.equal(view.querySelector('#inactiveTimeoutContainer').style.display, 'none');
    assert.equal(view.querySelector('#certThumbprintContainer').style.display, 'none');
    assert.equal(view.querySelector('#cspContainer').style.display, 'none');
    assert.equal(view.querySelector('#maxFailedLoginAttemptsContainer').style.display, 'none');

    view.querySelector('#isWaitingRoom').checked = true;
    view.querySelector('#enableHttps').checked = true;
    view.querySelector('#enableHsts').checked = true;
    view.querySelector('#enableSecurityHeaders').checked = true;
    view.querySelector('#enableAccountLockout').checked = true;
    controller.updateDependentFields(view);

    assert.equal(view.querySelector('#autoStartWhenReadyContainer').style.display, 'block');
    assert.equal(view.querySelector('#minReadyCountContainer').style.display, 'block');
    assert.equal(view.querySelector('#certThumbprintContainer').style.display, 'block');
    assert.equal(view.querySelector('#cspContainer').style.display, 'block');
    assert.equal(view.querySelector('#hstsMaxAgeContainer').style.display, 'block');
    assert.equal(view.querySelector('#maxFailedLoginAttemptsContainer').style.display, 'block');
});

test('embedded page exposes one ready-count input, accessible comboboxes, and mobile layout', () => {
    const html = fs.readFileSync(path.resolve(__dirname, '../Configuration/configPage.html'), 'utf8');
    const script = fs.readFileSync(path.resolve(__dirname, '../Configuration/configPage.js'), 'utf8');

    assert.equal((html.match(/id="minReadyCount"/g) || []).length, 1);
    assert.equal((html.match(/role="combobox"/g) || []).length, 3);
    assert.equal((html.match(/role="listbox"/g) || []).length, 3);
    assert.match(html, /@media \(max-width: 480px\)/);
    assert.match(html, /value="Anyone"[\s\S]*value="Host"[\s\S]*value="Vote"/);
    assert.match(html, /id="autoStartWhenReadyContainer"[\s\S]*?id="autoStartWhenReady"/);
    assert.match(html, /id="inactiveTimeoutContainer"[\s\S]*?id="inactiveTimeoutMinutes"/);
    assert.doesNotMatch(script, /(?:itemSelect|itemInput)\.innerHTML/);
    assert.doesNotMatch(html, /id="(?:hostOnlySeek|lockSeekAhead|enableNetworkLatencyCompensation|networkLatencyMeasurementIntervalSeconds|autoAdjustForLatency|maxLatencyCompensationMs)"/);
    assert.doesNotMatch(script, /querySelector\('#(?:hostOnlySeek|lockSeekAhead|enableNetworkLatencyCompensation|networkLatencyMeasurementIntervalSeconds|autoAdjustForLatency|maxLatencyCompensationMs)'\)/);
    assert.doesNotMatch(html, /id="enableDebugLogging"/);
    assert.match(html, /id="maxAuditLogEntries" min="0" max="100000"/);
    assert.match(html, /id="listenAddress"[^>]+placeholder="127\.0\.0\.1"/);
    assert.doesNotMatch(script, /Promise\.all\(\[getPluginConfiguration\(\),\s*loadLibraries\(\)\]\)/);
    assert.match(script, /const usersPromise = loadUsers\(\);/);
    assert.match(script, /enableExternalWebServer'\)\.checked = config\.EnableExternalWebServer === true/);
    assert.match(script, /class="button-flat btnStartParty"/);
    assert.match(script, /WatchParty\/\$\{encodeURIComponent\(partyId\)\}\/Start/);
    assert.doesNotMatch(script, /data-partyid="\$\{party\.Id\}"/);
});
