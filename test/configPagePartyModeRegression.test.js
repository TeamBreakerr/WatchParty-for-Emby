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
        ajax: async options => options,
        getJSON: async () => ({ Items: [] }),
        getItems: async () => ({ Items: [] }),
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

    add('#contentSourceMode', { value: 'recent' });
    add('#mediaVersionContainer');
    add('#mediaVersionId');
    add('#selectedMediaSourceId');
    add('#selectedMediaVersionItemId');
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
    add('#defaultMasterUser', { value: '' });
    add('#partyPassword', { value: '' });
    add('#isPartyActive', { checked: false });
    add('#isWaitingRoom', { checked: false });
    add('#autoStartWhenReady', { checked: false });
    add('#minReadyCount', { value: '1' });
    add('#maxBufferThresholdSeconds', { value: '30' });
    add('#autoKickInactive', { checked: false });
    add('#inactiveTimeoutMinutes', { value: '15' });
    add('#syncIntervalSeconds', { value: '5', min: '1', max: '60' });
    add('#syncOffsetMilliseconds', { value: '1000', min: '-10000', max: '10000' });
    add('#externalWebServerPort', { value: '8097', min: '1024', max: '65535' });
    add('#sessionExpirationMinutes', { value: '60', min: '5', max: '1440' });
    add('#rateLimitRequestsPerMinute', { value: '60', min: '0', max: '1000' });
    add('#rateLimitBlockDurationMinutes', { value: '15', min: '1', max: '1440' });
    add('#hstsMaxAge', { value: '31536000', min: '0', max: '63072000' });
    add('#maxFailedLoginAttempts', { value: '5', min: '1', max: '20' });
    add('#lockoutDurationMinutes', { value: '15', min: '1', max: '1440' });
    add('#lockoutWindowMinutes', { value: '10', min: '1', max: '60' });
    add('#maxAuditLogEntries', { value: '1000', min: '0', max: '100000' });
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

test('Series Party keeps a concrete source on the selected starting episode only', async () => {
    const view = createView();
    view.querySelector('#selectedSeasonId').value = 'season-1';
    view.querySelector('#selectedEpisodeId').value = 's1e2';
    view.querySelector('#searchEpisode').value = 'S1E2 - Second';
    view.querySelector('#selectedMediaSourceId').value = 'source-s1e2';
    view.querySelector('#selectedMediaVersionItemId').value = 's1e2-version';

    const result = await submit(view);

    assert.equal(result.updatedConfigurations.length, 1, result.toasts.join('\n'));
    const party = result.updatedConfigurations[0].WatchParties[0];
    assert.equal(party.MediaSourceId, null);
    assert.equal(party.EpisodeQueue.find(episode => episode.ItemId === 's1e2').MediaSourceId, 'source-s1e2');
    assert.equal(party.EpisodeQueue.find(episode => episode.ItemId === 's1e1').MediaSourceId, null);
});

test('single-episode Party still requires an episode', async () => {
    const view = createView();
    view.querySelector('#isSeriesParty').checked = false;

    const result = await submit(view);

    assert.match(result.toasts.join('\n'), /单集电视剧房间必须选择一集/);
    assert.equal(result.updatedConfigurations.length, 0);
});

test('switching to unified library search removes stale selection state without requiring a library id', async () => {
    const view = createView();
    view.querySelector('#contentSourceMode').value = 'library';
    const toasts = [];
    const updatedConfigurations = [];
    const controller = loadController(toasts, updatedConfigurations);
    controller.currentSeriesId = 'old-series';
    controller.currentSeasonId = 'old-season';
    view.querySelector('#selectedSeasonId').value = 'old-season';
    view.querySelector('#selectedEpisodeId').value = 'old-episode';

    controller.onContentSourceChange(view, 'library');

    assert.equal(controller.currentSeriesId, null);
    assert.equal(controller.currentSeasonId, null);
    assert.equal(view.querySelector('#selectedItemId').value, '');
    assert.equal(view.querySelector('#selectedSeasonId').value, '');
    assert.equal(view.querySelector('#selectedEpisodeId').value, '');

    view.querySelector('#selectedItemId').value = 'stale-item';
    controller.saveData(view);
    await new Promise(resolve => setTimeout(resolve, 10));

    assert.doesNotMatch(toasts.join('\n'), /请选择内容媒体库/);
    assert.equal(updatedConfigurations.length, 1);
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

test('Movie Party persists the selected concrete media version', async () => {
    const view = createView();
    view.querySelector('#selectedItemId').value = 'movie-parent';
    view.querySelector('#selectedItemId').dataset = {
        type: 'Movie',
        name: 'Concrete Movie'
    };
    view.querySelector('#searchContent').value = 'Concrete Movie';
    view.querySelector('#selectedMediaSourceId').value = 'source-1080p';
    view.querySelector('#selectedMediaVersionItemId').value = 'movie-1080p';
    view.querySelector('#isSeriesParty').checked = false;

    const result = await submit(view);

    assert.equal(result.updatedConfigurations.length, 1, result.toasts.join('\n'));
    const party = result.updatedConfigurations[0].WatchParties[0];
    assert.equal(party.ItemId, 'movie-1080p');
    assert.equal(party.MediaSourceId, 'source-1080p');
});

test('recently watched content creates a room without selecting a library first', async () => {
    const view = createView();
    view.querySelector('#contentSourceMode').value = 'recent';
    view.querySelector('#selectedLibraryId').value = '';
    view.querySelector('#selectedItemId').value = 'recent-movie';
    view.querySelector('#selectedItemId').dataset = {
        type: 'Movie',
        name: 'Recently Watched',
        libraryId: 'movies-library'
    };
    view.querySelector('#searchContent').value = 'Recently Watched';
    view.querySelector('#isSeriesParty').checked = false;

    const result = await submit(view);

    assert.equal(result.updatedConfigurations.length, 1, result.toasts.join('\n'));
    const party = result.updatedConfigurations[0].WatchParties[0];
    assert.equal(party.ItemId, 'recent-movie');
    assert.equal(party.LibraryId, 'movies-library');
});

test('recently watched is the default content source and requests DatePlayed order', async () => {
    const view = createView();
    const controller = loadController([], []);
    const receivedParams = [];
    global.ApiClient.getItems = async (_userId, params) => {
        receivedParams.push(params);
        return {
            Items: params.IsResumable
                ? [{ Id: 'episode-1', Name: 'Episode One', Type: 'Episode', SeriesId: 'series-1', SeriesName: 'Series One' }]
                : [{ Id: 'movie-1', Name: 'Movie One', Type: 'Movie', ParentId: 'library-1' }],
            TotalRecordCount: 1
        };
    };

    await controller.loadRecentContent(view);

    assert.equal(receivedParams.length, 2);
    assert.ok(receivedParams.every(params => params.SortBy === 'DatePlayed'));
    assert.ok(receivedParams.every(params => params.SortOrder === 'Descending'));
    assert.ok(receivedParams.some(params => params.IsPlayed === true));
    assert.ok(receivedParams.some(params => params.IsResumable === true));
    assert.equal(controller.searchContent_items[0].id, 'series-1');
    assert.equal(controller.searchContent_items[0].type, 'Series');
});

test('late recently-watched response cannot overwrite library mode', async () => {
    const view = createView();
    const controller = loadController([], []);
    const resolveRecent = [];
    global.ApiClient.getItems = () => new Promise(resolve => {
        resolveRecent.push(resolve);
    });
    controller.contentSourceMode = 'recent';

    const request = controller.loadRecentContent(view);
    controller.onContentSourceChange(view, 'library');
    resolveRecent.forEach(resolve => resolve({ Items: [], TotalRecordCount: 0 }));
    await request;

    assert.equal(controller.contentSourceMode, 'library');
    assert.deepEqual(controller.searchContent_items, []);
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

test('new Party payload only submits master-authoritative playback controls', async () => {
    const result = await submit(createView());
    const party = result.updatedConfigurations[0].WatchParties[0];

    assert.equal('PauseControl' in party, false);
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
    assert.equal(view.querySelector('#hstsMaxAge').disabled, true);
    assert.equal(view.querySelector('#maxFailedLoginAttempts').disabled, true);
    assert.equal(view.querySelector('#lockoutDurationMinutes').disabled, true);
    assert.equal(view.querySelector('#lockoutWindowMinutes').disabled, true);

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
    assert.equal(view.querySelector('#hstsMaxAge').disabled, false);
    assert.equal(view.querySelector('#maxFailedLoginAttempts').disabled, false);
    assert.equal(view.querySelector('#lockoutDurationMinutes').disabled, false);
    assert.equal(view.querySelector('#lockoutWindowMinutes').disabled, false);
});

test('room draft summary reflects the content, master, and launch mode', () => {
    const view = createView();
    const controller = loadController([], []);

    controller.updateRoomDraftSummary(view);

    const summary = view.querySelector('#roomDraftSummary').textContent;
    assert.match(summary, /Example Series/);
    assert.match(summary, /整部剧集/);
    assert.match(summary, /master-user/);
    assert.match(summary, /50 人/);
    assert.match(summary, /创建后停用/);
});

test('room overview count stays in sync with the rendered room list', () => {
    const view = createView();
    const controller = loadController([], []);

    controller.renderPartyList(view, {
        WatchParties: [
            { Id: 'active', ItemName: 'Movie A', ItemType: 'Movie', IsActive: true, CreatedDate: '2026-08-22T00:00:00Z' },
            { Id: 'inactive', ItemName: 'Movie B', ItemType: 'Movie', IsActive: false, CreatedDate: '2026-08-22T00:00:00Z' }
        ]
    });

    assert.equal(view.querySelector('#partyCount').textContent, '2');
    assert.match(view.querySelector('#activePartiesList').innerHTML, /watch-party-list/);
    assert.match(view.querySelector('#activePartiesList').innerHTML, /1 个启用/);
});

test('room overview separates room clients from selectable online iOS sessions', () => {
    const view = createView();
    const controller = loadController([], []);
    controller.escapeHtml = value => String(value ?? '');
    controller.partyRuntimeById = new Map([
        ['active', {
            Id: 'active',
            IsPlaying: true,
            CurrentPositionTicks: 1250000000,
            Participants: [
                {
                    UserName: 'xsq',
                    Client: 'Emby for iOS',
                    IsOnline: true,
                    HasActiveWebSocket: true,
                    CanReceiveCommands: true,
                    IsHost: false
                }
            ]
        }]
    ]);
    controller.launchTargetsByParty = new Map([
        ['active', [
            {
                SessionId: 'online-session-12345678',
                UserName: 'friend',
                DeviceName: 'iPhone 16',
                Client: 'Emby for iOS',
                InRoom: false,
                CanLaunch: true,
                Message: '在线 · 可拉入房间'
            },
            {
                SessionId: 'stale-session-87654321',
                UserName: 'old client',
                DeviceName: 'iPhone 13',
                Client: 'Emby for iOS',
                InRoom: true,
                CanLaunch: false,
                Message: '在线，但控制连接未建立'
            }
        ]]
    ]);
    controller.updateLaunchTargetSelection('active', 'online-session-12345678', true);

    controller.renderPartyList(view, {
        WatchParties: [
            { Id: 'active', ItemName: 'Movie A', ItemType: 'Movie', IsActive: true, CreatedDate: '2026-08-22T00:00:00Z' }
        ]
    });

    const html = view.querySelector('#activePartiesList').innerHTML;
    assert.match(html, /xsq/);
    assert.match(html, /Emby for iOS/);
    assert.match(html, /可控制/);
    assert.match(html, /在线官方 iOS Session/);
    assert.match(html, /iPhone 16/);
    assert.match(html, /online-session-12345678[^>]* checked/);
    assert.match(html, /stale-session-87654321[^>]* disabled/);
    assert.match(html, /btnSyncParty/);
    assert.match(html, /一键开播/);
    assert.doesNotMatch(html, /一键同步/);
});

test('one-click launch posts only explicitly selected session ids', async () => {
    const view = createView();
    const toasts = [];
    const controller = loadController(toasts, []);
    let request;
    global.ApiClient.ajax = async options => {
        request = options;
        return { Accepted: true, Message: '已向 1 台客户端发送开播命令' };
    };
    controller.refreshPartyRuntimeStatus = async () => [];

    await controller.syncParty(view, 'room-1', ['ios-selected']);

    assert.equal(request.type, 'POST');
    assert.equal(request.url, 'WatchParty/room-1/Sync');
    assert.equal(request.dataType, 'json');
    assert.equal(request.contentType, 'application/json');
    assert.deepEqual(JSON.parse(request.data), { SessionIds: ['ios-selected'] });
    assert.match(toasts.join('\n'), /已向 1 台客户端发送开播命令/);
});

test('one-click launch refuses an empty selection without sending a request', async () => {
    const view = createView();
    const toasts = [];
    const controller = loadController(toasts, []);
    let requestCount = 0;
    global.ApiClient.ajax = async () => { requestCount++; };

    const result = await controller.syncParty(view, 'room-1', []);

    assert.equal(requestCount, 0);
    assert.equal(result.Accepted, false);
    assert.match(toasts.join('\n'), /请先勾选至少一个在线 iOS Session/);
});

test('room count and default-disabled console status settle before ancillary data finishes loading', async () => {
    const configuration = {
        WatchParties: [
            { Id: 'configured-room', ItemName: 'Configured Room', ItemType: 'Movie', IsActive: true, CreatedDate: '2026-08-22T00:00:00Z' }
        ]
    };
    const view = createView();
    const controller = loadController([], [], configuration);
    const never = new Promise(() => {});
    global.ApiClient.getUsers = () => never;
    global.ApiClient.getJSON = () => never;

    controller.loadData(view);
    await new Promise(resolve => setTimeout(resolve, 20));

    assert.equal(view.querySelector('#partyCount').textContent, '1');
    assert.match(view.querySelector('#heroServerStatusText').textContent, /未启用/);
    assert.doesNotMatch(view.querySelector('#heroServerStatusText').textContent, /正在检查/);
});

test('leaving the page before configuration loads does not start runtime polling', async () => {
    const view = createView();
    const controller = loadController([], []);
    let resolveConfiguration;
    let pollingStarted = 0;
    global.ApiClient.getPluginConfiguration = () => new Promise(resolve => {
        resolveConfiguration = resolve;
    });
    controller.startPartyRuntimeRefresh = () => { pollingStarted++; };

    controller.loadData(view);
    controller.onPause();
    resolveConfiguration({ WatchParties: [] });
    await new Promise(resolve => setTimeout(resolve, 0));

    assert.equal(pollingStarted, 0);
});

test('configured default master takes precedence over the current user', async () => {
    const view = createView();
    view.querySelector('#masterUser').value = '';
    const controller = loadController([], [], {
        WatchParties: [],
        DefaultMasterUserId: 'team-id'
    });
    global.ApiClient.getCurrentUserId = () => 'home-id';
    global.ApiClient.getUsers = async () => [
        { Id: 'home-id', Name: 'home' },
        { Id: 'team-id', Name: 'Team Breaker' }
    ];

    controller.loadData(view);
    await new Promise(resolve => setTimeout(resolve, 20));

    assert.equal(view.querySelector('#masterUser').value, 'team-id');
    assert.equal(view.querySelector('#masterUser').dataset.defaultUserId, 'team-id');
    assert.equal(view.querySelector('#defaultMasterUser').value, 'team-id');
});

test('master defaults to the current user when no default is configured', async () => {
    const view = createView();
    view.querySelector('#masterUser').value = '';
    const controller = loadController([], [], { WatchParties: [] });
    global.ApiClient.getCurrentUserId = () => 'home-id';
    global.ApiClient.getUsers = async () => [
        { Id: 'home-id', Name: 'home' },
        { Id: 'team-id', Name: 'Team Breaker' }
    ];

    controller.loadData(view);
    await new Promise(resolve => setTimeout(resolve, 20));

    assert.equal(view.querySelector('#masterUser').value, 'home-id');
    assert.equal(view.querySelector('#masterUser').dataset.defaultUserId, 'home-id');
    assert.equal(view.querySelector('#defaultMasterUser').value, '');
});

test('missing configured default master falls back to the current user', async () => {
    const view = createView();
    view.querySelector('#masterUser').value = '';
    const controller = loadController([], [], {
        WatchParties: [],
        DefaultMasterUserId: 'deleted-user-id'
    });
    global.ApiClient.getCurrentUserId = () => 'home-id';
    global.ApiClient.getUsers = async () => [
        { Id: 'home-id', Name: 'home' },
        { Id: 'team-id', Name: 'Team Breaker' }
    ];

    controller.loadData(view);
    await new Promise(resolve => setTimeout(resolve, 20));

    assert.equal(view.querySelector('#masterUser').value, 'home-id');
    assert.equal(view.querySelector('#masterUser').dataset.defaultUserId, 'home-id');
    assert.equal(view.querySelector('#defaultMasterUser').value, '');
});

test('changing the default master updates and resets the room draft selection', async () => {
    const view = createView();
    const controller = loadController([], [], { WatchParties: [] });
    global.ApiClient.getCurrentUserId = () => 'home-id';
    global.ApiClient.getUsers = async () => [
        { Id: 'home-id', Name: 'home' },
        { Id: 'team-id', Name: 'Team Breaker' }
    ];

    controller.loadData(view);
    await new Promise(resolve => setTimeout(resolve, 20));

    view.querySelector('#defaultMasterUser').value = 'team-id';
    controller.applyDefaultMasterToRoomDraft(view);
    assert.equal(view.querySelector('#masterUser').value, 'team-id');
    assert.equal(view.querySelector('#masterUser').dataset.defaultUserId, 'team-id');
    assert.match(view.querySelector('#roomDraftSummary').textContent, /Master：Team Breaker/);

    controller.resetCreatePartyForm(view);
    assert.equal(view.querySelector('#masterUser').value, 'team-id');
});

test('external console status times out instead of remaining pending forever', async () => {
    const view = createView();
    const controller = loadController([], []);
    controller.statusCheckTimeoutMs = 5;
    const nativeFetch = global.fetch;
    global.fetch = (_url, options = {}) => new Promise((_resolve, reject) => {
        options.signal?.addEventListener('abort', () => reject(new Error('aborted')));
    });

    try {
        controller.checkWebServerStatus(view, {
            EnableExternalWebServer: true,
            UseReverseProxy: false,
            ExternalWebServerPort: 8097,
            EmbyServerUrl: 'http://127.0.0.1:8096'
        });
        await new Promise(resolve => setTimeout(resolve, 30));
    } finally {
        global.fetch = nativeFetch;
    }

    assert.equal(view.querySelector('#heroServerStatusText').textContent, '未启用');
    assert.match(view.querySelector('#webServerStatusText').innerHTML, /连接检查超时/);
});

test('room metric displays only an Arabic numeral without a unit suffix', () => {
    const html = fs.readFileSync(path.resolve(__dirname, '../Configuration/configPage.html'), 'utf8');

    assert.match(html, /class="watch-party-metric-value" id="partyCount">—<\/span>/);
    assert.doesNotMatch(html, /id="partyCount">—<\/span>\s*个/);
});

test('saving global settings exposes persistent success feedback', async () => {
    const view = createView();
    const updatedConfigurations = [];
    const controller = loadController([], updatedConfigurations);
    controller.checkWebServerStatus = () => {};
    view.querySelector('#defaultMasterUser').value = 'team-id';

    controller.saveGlobalSettings(view);
    await new Promise(resolve => setTimeout(resolve, 20));

    assert.match(view.querySelector('#configSaveFeedback').textContent, /设置已保存/);
    assert.equal(view.querySelector('#configSaveFeedback').dataset.tone, 'success');
    assert.equal(updatedConfigurations[0].DefaultMasterUserId, 'team-id');
});

test('global settings reject out-of-range numeric values before saving', async () => {
    const view = createView();
    const toasts = [];
    const updatedConfigurations = [];
    const controller = loadController(toasts, updatedConfigurations);
    view.querySelector('#syncIntervalSeconds').value = '999';

    controller.saveGlobalSettings(view);
    await new Promise(resolve => setTimeout(resolve, 20));

    assert.equal(updatedConfigurations.length, 0);
    assert.match(toasts.join('\n'), /同步检查间隔必须在 1 到 60 之间/);
    assert.equal(view.querySelector('#configSaveFeedback').dataset.tone, 'error');
});

test('hidden advanced numeric settings do not block saving', async () => {
    const view = createView();
    const toasts = [];
    const updatedConfigurations = [];
    const controller = loadController(toasts, updatedConfigurations);
    view.querySelector('#enableHttps').checked = false;
    view.querySelector('#enableHsts').checked = false;
    view.querySelector('#hstsMaxAge').value = '999999999';
    view.querySelector('#enableAccountLockout').checked = false;
    view.querySelector('#maxFailedLoginAttempts').value = '999';

    controller.saveGlobalSettings(view);
    await new Promise(resolve => setTimeout(resolve, 20));

    assert.equal(updatedConfigurations.length, 1, toasts.join('\n'));
    assert.equal(view.querySelector('#configSaveFeedback').dataset.tone, 'success');
});

test('embedded page presents a task-oriented configuration workspace', () => {
    const html = fs.readFileSync(path.resolve(__dirname, '../Configuration/configPage.html'), 'utf8');

    assert.match(html, /class="watch-party-hero"/);
    assert.match(html, /<nav[^>]+aria-label="配置页面导航"/);
    assert.match(html, /href="#partyOverview"/);
    assert.match(html, /href="#createParty"/);
    assert.match(html, /href="#syncSettings"/);
    assert.match(html, /href="#externalConsole"/);
    assert.match(html, /href="#securitySettings"/);

    ['partyOverview', 'createParty', 'syncSettings', 'externalConsole', 'securitySettings', 'usageHelp']
        .forEach(sectionId => assert.match(html, new RegExp(`id="${sectionId}"`)));

    assert.equal((html.match(/class="setup-step/g) || []).length, 4);
    assert.match(html, /<details[^>]+id="roomAdvancedSettings"/);
    assert.match(html, /<details[^>]+id="securityAdvancedSettings"/);
    assert.match(html, /id="btnCreateParty"/);
    assert.match(html, /id="contentSourceMode"/);
    assert.match(html, /value="recent"[^>]*selected/);
    assert.match(html, /id="configSaveFeedback"[^>]+role="status"[^>]+aria-live="polite"/);
    assert.match(html, /id="partyCount"/);
    assert.match(html, /id="heroServerStatusText"[^>]+role="status"[^>]+aria-live="polite"/);
    assert.match(html, /id="webServerStatusText"[^>]+role="status"[^>]+aria-live="polite"/);
    assert.match(html, /id="partyRuntimeStatus"[^>]+role="status"[^>]+aria-live="polite"/);
    assert.match(html, /id="activePartiesList"[^>]+role="region"/);
    assert.match(html, /@media \(max-width: 760px\)/);
    assert.match(html, /@media \(prefers-reduced-motion: reduce\)/);
});

test('plugin page is registered in both the admin navigation and homepage user menu', () => {
    const source = fs.readFileSync(path.resolve(__dirname, '../Plugin.cs'), 'utf8');

    assert.match(source, /EnableInMainMenu\s*=\s*true/);
    assert.match(source, /EnableInUserMenu\s*=\s*true/);
    assert.match(source, /DisplayName\s*=\s*"一起看控制台"/);
});

test('embedded page stays legible and uses the full Emby settings width in a light theme', () => {
    const html = fs.readFileSync(path.resolve(__dirname, '../Configuration/configPage.html'), 'utf8');
    const pageRule = html.match(/\.watch-party-page\s*\{([\s\S]*?)\n\s*\}/);
    const shellRule = html.match(/\.watch-party-shell\s*\{([\s\S]*?)\n\s*\}/);
    const formRule = html.match(/\.watchPartyConfigForm\s*\{([\s\S]*?)\n\s*\}/);
    const luminance = hex => {
        const channels = hex.match(/[0-9a-f]{2}/gi).map(channel => parseInt(channel, 16) / 255);
        const linear = channels.map(channel => channel <= 0.04045
            ? channel / 12.92
            : ((channel + 0.055) / 1.055) ** 2.4);
        return (0.2126 * linear[0]) + (0.7152 * linear[1]) + (0.0722 * linear[2]);
    };
    const contrast = (first, second) => {
        const [lighter, darker] = [luminance(first), luminance(second)].sort((a, b) => b - a);
        return (lighter + 0.05) / (darker + 0.05);
    };
    const variable = name => pageRule[1].match(new RegExp(`--${name}:\\s*(#[0-9a-f]{6});`, 'i'))?.[1];

    assert.ok(pageRule, 'watch-party-page styles should exist');
    assert.ok(shellRule, 'watch-party-shell styles should exist');
    assert.ok(formRule, 'watchPartyConfigForm styles should exist');
    assert.doesNotMatch(html, /class="readOnlyContent auto-center watch-party-page"/);
    assert.match(pageRule[1], /--wp-surface:\s*#fff(?:fff)?;/i);
    assert.match(pageRule[1], /--wp-text:\s*#[0-9a-f]{6};/i);
    assert.match(pageRule[1], /max-width:\s*none\s*!important;/);
    assert.match(pageRule[1], /width:\s*100%\s*!important;/);
    assert.match(pageRule[1], /margin:\s*0\s*!important;/);
    assert.match(pageRule[1], /color:\s*var\(--wp-text\);/);
    assert.doesNotMatch(pageRule[1], /color:\s*inherit/);
    assert.match(shellRule[1], /box-sizing:\s*border-box;/);
    assert.match(formRule[1], /max-width:\s*none\s*!important;/);
    assert.match(formRule[1], /width:\s*100%\s*!important;/);
    assert.ok(contrast(variable('wp-surface'), variable('wp-text')) >= 7, 'primary text should meet enhanced contrast');
    assert.ok(contrast(variable('wp-surface'), variable('wp-text-muted')) >= 4.5, 'secondary text should meet normal contrast');
    assert.ok(contrast('#ffffff', variable('wp-action-bg')) >= 4.5, 'primary action text should meet normal contrast');
    assert.doesNotMatch(html, /#202832|#17211f|#1b232d|background:\s*rgba\(24,\s*27,\s*32/);
});

test('form controls and the dashboard action use polished interaction states', () => {
    const html = fs.readFileSync(path.resolve(__dirname, '../Configuration/configPage.html'), 'utf8');
    const controlRule = html.match(/\.watch-party-page input:not\(\[type="checkbox"\]\):not\(\[type="hidden"\]\),\s*\n\s*\.watch-party-page select\s*\{([\s\S]*?)\n\s*\}/);
    const actionRule = html.match(/\.watch-party-primary-action\s*\{([\s\S]*?)\n\s*\}/);

    assert.ok(controlRule, 'shared input and select styles should exist');
    assert.ok(actionRule, 'dashboard action styles should exist');
    assert.match(controlRule[1], /min-height:\s*46px;/);
    assert.match(controlRule[1], /border-radius:\s*10px\s*!important;/);
    assert.match(controlRule[1], /transition:/);
    assert.match(html, /\.watch-party-page input::placeholder\s*\{/);
    assert.match(html, /\.watch-party-page input:not\([^}]+:hover,[\s\S]*?\.watch-party-page select:hover\s*\{/);
    assert.match(html, /\.watch-party-page input:not\([^}]+:focus,[\s\S]*?\.watch-party-page select:focus\s*\{[\s\S]*?box-shadow:\s*0 0 0 3px/);
    assert.match(html, /\.watch-party-page input:disabled,[\s\S]*?\.watch-party-page select:disabled\s*\{/);
    assert.match(actionRule[1], /border-radius:\s*10px\s*!important;/);
    assert.match(actionRule[1], /background:\s*var\(--wp-action-bg\)\s*!important;/);
    assert.doesNotMatch(actionRule[1], /999px|linear-gradient/);
    assert.match(html, /\.watch-party-primary-action:hover\s*\{/);
    assert.match(html, /\.watch-party-primary-action:active\s*\{/);
});

test('single-line selects keep a fixed height and do not overlap following fields', () => {
    const html = fs.readFileSync(path.resolve(__dirname, '../Configuration/configPage.html'), 'utf8');

    assert.match(html, /\.watch-party-page select:not\(\[multiple\]\)\s*\{[\s\S]*?height:\s*46px\s*!important;[\s\S]*?line-height:\s*1\.25\s*!important;/);
    assert.match(html, /\.watch-party-page select\[multiple\]\s*\{[\s\S]*?height:\s*auto\s*!important;/);
    assert.match(html, /\.watch-party-field-grid \.selectContainer\s*\{[\s\S]*?align-self:\s*start;/);
});

test('embedded page exposes one ready-count input, accessible comboboxes, and mobile layout', () => {
    const html = fs.readFileSync(path.resolve(__dirname, '../Configuration/configPage.html'), 'utf8');
    const script = fs.readFileSync(path.resolve(__dirname, '../Configuration/configPage.js'), 'utf8');

    assert.equal((html.match(/id="minReadyCount"/g) || []).length, 1);
    assert.equal((html.match(/role="combobox"/g) || []).length, 3);
    assert.equal((html.match(/role="listbox"/g) || []).length, 3);
    assert.match(html, /@media \(max-width: 480px\)/);
    assert.doesNotMatch(html, /id="pauseControl"|value="Anyone"|value="Vote"/);
    assert.doesNotMatch(script, /PauseControl|pauseControl|normalizePauseControl/);
    assert.match(html, /播放、暂停、进度和切集均以主控用户为准/);
    assert.match(html, /id="syncToleranceSeconds"[^>]+value="2"/);
    assert.match(html, /id="defaultMasterUser"/);
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
