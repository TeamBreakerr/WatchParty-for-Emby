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

function loadController(
    toasts,
    updatedConfigurations,
    configuration = { WatchParties: [] },
    dependencies = {}) {
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
            dependencies.loading || { show() {}, hide() {} },
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
    add('#mediaVersionPath');
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
    add('#isPartyActive', { checked: false });
    add('#isWaitingRoom', { checked: false });
    add('#autoStartWhenReady', { checked: false });
    add('#minReadyCount', { value: '1' });
    add('#maxBufferThresholdSeconds', { value: '30' });
    add('#autoKickInactive', { checked: false });
    add('#inactiveTimeoutMinutes', { value: '15' });
    add('#syncIntervalSeconds', { value: '5', min: '1', max: '60' });
    add('#syncOffsetMilliseconds', { value: '1000', min: '-10000', max: '10000' });
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

test('single-episode Party keeps the logical episode identity with a concrete media source', async () => {
    const view = createView();
    view.querySelector('#isSeriesParty').checked = false;
    view.querySelector('#selectedEpisodeId').value = 's1e2';
    view.querySelector('#searchEpisode').value = 'S1E2 - Second';
    view.querySelector('#selectedMediaSourceId').value = 'source-s1e2';
    view.querySelector('#selectedMediaVersionItemId').value = 's1e2-version';

    const result = await submit(view);

    assert.equal(result.updatedConfigurations.length, 1, result.toasts.join('\n'));
    const party = result.updatedConfigurations[0].WatchParties[0];
    assert.equal(party.ItemId, 's1e2');
    assert.equal(party.MediaSourceId, 'source-s1e2');
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

test('Movie Party keeps its logical item identity while persisting the selected media source', async () => {
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
    assert.equal(party.ItemId, 'movie-parent');
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

test('successful creation completely clears content, identity, and whitelist selections', async () => {
    const view = createView();
    const selectedViewer = element({ value: 'viewer-user', selected: true });
    view.querySelector('#allowedUsers').options = [selectedViewer];

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
    assert.equal(selectedViewer.selected, false);
    assert.equal(view.querySelector('#selectedItemId').innerHTML, '');
});

test('global settings preserve legitimate zero values', async () => {
    const view = createView();
    view.querySelector('#syncOffsetMilliseconds').value = '0';

    const toasts = [];
    const updatedConfigurations = [];
    const controller = loadController(toasts, updatedConfigurations);
    controller.saveGlobalSettings(view);
    await new Promise(resolve => setTimeout(resolve, 20));

    assert.equal(updatedConfigurations.length, 1, toasts.join('\n'));
    assert.equal(updatedConfigurations[0].SyncOffsetMilliseconds, 0);
});

test('loading global settings preserves legitimate zero values', async () => {
    const configuration = {
        WatchParties: [],
        SyncOffsetMilliseconds: 0
    };
    const view = createView();
    const controller = loadController([], [], configuration);

    controller.loadData(view);
    await new Promise(resolve => setTimeout(resolve, 20));

    assert.equal(view.querySelector('#syncOffsetMilliseconds').value, 0);
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

test('dependent room fields are progressively disclosed', () => {
    const view = createView();
    const controller = loadController([], []);

    view.querySelector('#isWaitingRoom').checked = false;
    view.querySelector('#autoStartWhenReady').checked = true;
    view.querySelector('#autoKickInactive').checked = false;
    controller.updateDependentFields(view);

    assert.equal(view.querySelector('#autoStartWhenReadyContainer').style.display, 'none');
    assert.equal(view.querySelector('#minReadyCountContainer').style.display, 'none');
    assert.equal(view.querySelector('#inactiveTimeoutContainer').style.display, 'none');

    view.querySelector('#isWaitingRoom').checked = true;
    controller.updateDependentFields(view);

    assert.equal(view.querySelector('#autoStartWhenReadyContainer').style.display, 'block');
    assert.equal(view.querySelector('#minReadyCountContainer').style.display, 'block');
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

test('room overview separates room clients from selectable online sessions on every client type', () => {
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
                SessionId: 'master-web-session-11223344',
                UserName: 'team breaker',
                DeviceName: 'Safari on Mac',
                Client: 'Emby Web',
                IsMaster: true,
                InRoom: false,
                CanLaunch: true,
                Message: '在线 · 可开播'
            },
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
                IsOnline: false,
                Message: '客户端已离线'
            },
            {
                SessionId: 'shared-web-session-55667788',
                UserName: 'team breaker',
                DeviceName: 'Chromium macOS',
                Client: 'Emby Web',
                IsMaster: true,
                InRoom: false,
                CanLaunch: false,
                HasAmbiguousWebControllers: true,
                ActiveControllerCount: 2,
                Message: '检测到 2 个活动 Web 控制连接共享此 Session；通常是同一浏览器打开了多个 Emby 标签页，请关闭多余标签页后重试'
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
    assert.match(html, /在线 Session/);
    assert.match(html, /Emby Web/);
    assert.match(html, /Safari on Mac/);
    assert.match(html, /Master/);
    assert.match(html, /iPhone 16/);
    assert.match(html, /online-session-12345678[^>]* checked/);
    assert.match(html, /stale-session-87654321[^>]* disabled/);
    assert.match(html, /shared-web-session-55667788[^>]* disabled/);
    assert.match(html, /2 个活动 Web 控制连接/);
    assert.match(html, /通常是同一浏览器打开了多个 Emby 标签页/);
    assert.match(html, /btnSyncParty/);
    assert.match(html, /一键开播/);
    assert.doesNotMatch(html, /一键同步/);
});

test('an offline dormant participant is displayed as offline, never as a missing control connection', () => {
    const controller = loadController([], []);

    const state = controller.participantControlState({
        Client: 'Emby for iOS',
        IsOnline: false,
        IsDormant: true,
        HasActiveWebSocket: false,
        CanReceiveCommands: false
    });

    assert.deepEqual(state, { text: '离线', className: 'is-warning' });
});

test('a reporting iOS participant without WebSocket is online but not controllable', () => {
    const controller = loadController([], []);

    const state = controller.participantControlState({
        Client: 'Emby for iOS',
        IsOnline: true,
        IsPaused: false,
        IsDormant: false,
        HasActiveWebSocket: false,
        CanReceiveCommands: false
    });

    assert.deepEqual(state, {
        text: '在线上报 · 控制连接已断开',
        className: 'is-warning'
    });
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
    assert.match(toasts.join('\n'), /请先勾选至少一个在线 Session/);
});

test('one-click launch never leaves a navigation-blocking global loading mask', () => {
    const loadingEvents = [];
    const controller = loadController([], [], { WatchParties: [] }, {
        loading: {
            show() { loadingEvents.push('show'); },
            hide() { loadingEvents.push('hide'); }
        }
    });
    global.ApiClient.ajax = () => new Promise(() => {});

    controller.syncParty(createView(), 'room-1', ['current-web-session']);

    assert.doesNotMatch(loadingEvents.join(','), /show/);
});

test('one-click launch uses button-local pending state until the request settles', async () => {
    const controller = loadController([], []);
    const label = element({ textContent: '一键开播' });
    const button = element({
        dataset: { partyid: 'room-1' },
        querySelector: selector => selector === 'span' ? label : null
    });
    let resolveRequest;
    global.ApiClient.ajax = () => new Promise(resolve => {
        resolveRequest = resolve;
    });
    controller.refreshPartyRuntimeStatus = async () => [];
    controller.updateLaunchTargetSelection('room-1', 'current-web-session', true);

    const pending = controller.syncParty(
        createView(),
        'room-1',
        ['current-web-session'],
        button);

    assert.equal(button.disabled, true);
    assert.equal(button.getAttribute('aria-busy'), 'true');
    assert.equal(label.textContent, '正在开播…');

    resolveRequest({ Accepted: true, Message: '已发送' });
    await pending;

    assert.equal(button.disabled, false);
    assert.equal(button.getAttribute('aria-busy'), null);
    assert.equal(label.textContent, '一键开播');
});

test('leaving the configuration view clears any outstanding global loading mask', () => {
    const loadingEvents = [];
    const controller = loadController([], [], { WatchParties: [] }, {
        loading: {
            show() { loadingEvents.push('show'); },
            hide() { loadingEvents.push('hide'); }
        }
    });

    controller.onPause();

    assert.deepEqual(loadingEvents, ['hide']);
});

test('room count settles before ancillary data finishes loading', async () => {
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

test('room metric displays only an Arabic numeral without a unit suffix', () => {
    const html = fs.readFileSync(path.resolve(__dirname, '../Configuration/configPage.html'), 'utf8');

    assert.match(html, /class="watch-party-metric-value" id="partyCount">—<\/span>/);
    assert.doesNotMatch(html, /id="partyCount">—<\/span>\s*个/);
});

test('saving global settings exposes persistent success feedback', async () => {
    const view = createView();
    const updatedConfigurations = [];
    const controller = loadController([], updatedConfigurations);
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

test('embedded page presents a task-oriented configuration workspace', () => {
    const html = fs.readFileSync(path.resolve(__dirname, '../Configuration/configPage.html'), 'utf8');

    assert.match(html, /class="watch-party-hero"/);
    assert.match(html, /<nav[^>]+aria-label="配置页面导航"/);
    assert.match(html, /href="#partyOverview"/);
    assert.match(html, /href="#createParty"/);
    assert.match(html, /href="#syncSettings"/);

    ['partyOverview', 'createParty', 'syncSettings', 'usageHelp']
        .forEach(sectionId => assert.match(html, new RegExp(`id="${sectionId}"`)));

    assert.equal((html.match(/class="setup-step/g) || []).length, 4);
    assert.match(html, /<details[^>]+id="roomAdvancedSettings"/);
    assert.match(html, /id="btnCreateParty"/);
    assert.match(html, /id="contentSourceMode"/);
    assert.match(html, /value="recent"[^>]*selected/);
    assert.match(html, /id="configSaveFeedback"[^>]+role="status"[^>]+aria-live="polite"/);
    assert.match(html, /id="partyCount"/);
    assert.match(html, /id="partyRuntimeStatus"[^>]+role="status"[^>]+aria-live="polite"/);
    assert.match(html, /id="activePartiesList"[^>]+role="region"/);
    assert.match(html, /@media \(max-width: 760px\)/);
    assert.match(html, /@media \(prefers-reduced-motion: reduce\)/);

    const ids = Array.from(html.matchAll(/\sid="([^"]+)"/g), match => match[1]);
    assert.equal(new Set(ids).size, ids.length, 'embedded page IDs must stay unique');
});

test('plugin keeps the existing admin menu item without adding a duplicate user-menu entry', () => {
    const source = fs.readFileSync(path.resolve(__dirname, '../Plugin.cs'), 'utf8');

    assert.match(source, /EnableInMainMenu\s*=\s*true/);
    assert.doesNotMatch(source, /EnableInUserMenu\s*=\s*true/);
    assert.match(source, /DisplayName\s*=\s*"一起看"/);
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

test('form controls use polished interaction states', () => {
    const html = fs.readFileSync(path.resolve(__dirname, '../Configuration/configPage.html'), 'utf8');
    const controlRule = html.match(/\.watch-party-page input:not\(\[type="checkbox"\]\):not\(\[type="hidden"\]\),\s*\n\s*\.watch-party-page select\s*\{([\s\S]*?)\n\s*\}/);

    assert.ok(controlRule, 'shared input and select styles should exist');
    assert.match(controlRule[1], /min-height:\s*46px;/);
    assert.match(controlRule[1], /border-radius:\s*10px\s*!important;/);
    assert.match(controlRule[1], /transition:/);
    assert.match(html, /\.watch-party-page input::placeholder\s*\{/);
    assert.match(html, /\.watch-party-page input:not\([^}]+:hover,[\s\S]*?\.watch-party-page select:hover\s*\{/);
    assert.match(html, /\.watch-party-page input:not\([^}]+:focus,[\s\S]*?\.watch-party-page select:focus\s*\{[\s\S]*?box-shadow:\s*0 0 0 3px/);
    assert.match(html, /\.watch-party-page input:disabled,[\s\S]*?\.watch-party-page select:disabled\s*\{/);
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
    assert.doesNotMatch(script, /Promise\.all\(\[getPluginConfiguration\(\),\s*loadLibraries\(\)\]\)/);
    assert.match(script, /const usersPromise = loadUsers\(\);/);
    assert.match(script, /class="button-flat btnStartParty"/);
    assert.match(script, /WatchParty\/\$\{encodeURIComponent\(partyId\)\}\/Start/);
    assert.doesNotMatch(script, /data-partyid="\$\{party\.Id\}"/);
});

test('retired external console has no UI, listener settings, or embedded resource', () => {
    const html = fs.readFileSync(path.resolve(__dirname, '../Configuration/configPage.html'), 'utf8');
    const script = fs.readFileSync(path.resolve(__dirname, '../Configuration/configPage.js'), 'utf8');
    const project = fs.readFileSync(path.resolve(__dirname, '../WatchPartyForEmby.csproj'), 'utf8');
    const plugin = fs.readFileSync(path.resolve(__dirname, '../Plugin.cs'), 'utf8');

    assert.doesNotMatch(html, /externalConsole|securitySettings|外部控制台|Emby API 密钥/);
    assert.doesNotMatch(script, /ExternalWebServer|checkWebServerStatus|getDashboardUrl|EmbyApiKey/);
    assert.doesNotMatch(project, /Configuration\\external\.html/);
    assert.doesNotMatch(plugin, /ExternalWebServer|StartWebServer|RestartWebServer/);
});

test('room actions are a centered full-width footer and media paths wrap in full', () => {
    const html = fs.readFileSync(path.resolve(__dirname, '../Configuration/configPage.html'), 'utf8');
    const cardRule = html.match(/\.watch-party-list-card\s*\{([\s\S]*?)\n\s*\}/);
    const actionRule = html.match(/\.watch-party-list-actions\s*\{([\s\S]*?)\n\s*\}/);
    const pathRule = html.match(/\.watch-party-media-version-path\s*\{([\s\S]*?)\n\s*\}/);

    assert.match(cardRule[1], /grid-template-columns:\s*minmax\(0, 1fr\);/);
    assert.match(actionRule[1], /justify-content:\s*center;/);
    assert.match(actionRule[1], /border-top:/);
    assert.match(pathRule[1], /white-space:\s*normal;/);
    assert.match(pathRule[1], /overflow-wrap:\s*anywhere;/);
    assert.doesNotMatch(pathRule[1], /text-overflow:\s*ellipsis|overflow:\s*hidden/);
});

test('media version selection exposes the concrete file path returned by Emby', async () => {
    const controller = loadController([], []);
    const view = createView();
    global.ApiClient.getJSON = async () => {
        return {
            Id: 'item-1',
            MediaSources: [
                {
                    Id: 'source-1',
                    ItemId: 'item-1',
                    Path: '/media/movies/example.mkv',
                    Name: 'Example 4K'
                }
            ]
        };
    };

    const versions = await controller.loadMediaVersions(view, 'item-1');

    assert.equal(versions.length, 1);
    assert.equal(view.querySelector('#mediaVersionId').options[0].dataset.path, '/media/movies/example.mkv');
    assert.equal(view.querySelector('#mediaVersionPath').textContent, '/media/movies/example.mkv');
    assert.equal(view.querySelector('#mediaVersionPath').style.display, 'block');
});
