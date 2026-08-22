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

test('saving global settings exposes persistent success feedback', async () => {
    const view = createView();
    const controller = loadController([], []);
    controller.checkWebServerStatus = () => {};

    controller.saveGlobalSettings(view);
    await new Promise(resolve => setTimeout(resolve, 20));

    assert.match(view.querySelector('#configSaveFeedback').textContent, /设置已保存/);
    assert.equal(view.querySelector('#configSaveFeedback').dataset.tone, 'success');
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
    assert.match(html, /id="configSaveFeedback"[^>]+role="status"[^>]+aria-live="polite"/);
    assert.match(html, /id="partyCount"/);
    assert.match(html, /id="heroServerStatusText"[^>]+role="status"[^>]+aria-live="polite"/);
    assert.match(html, /id="webServerStatusText"[^>]+role="status"[^>]+aria-live="polite"/);
    assert.match(html, /@media \(max-width: 760px\)/);
    assert.match(html, /@media \(prefers-reduced-motion: reduce\)/);
});

test('embedded page stays legible and uses the full Emby settings width in a light theme', () => {
    const html = fs.readFileSync(path.resolve(__dirname, '../Configuration/configPage.html'), 'utf8');
    const pageRule = html.match(/\.watch-party-page\s*\{([\s\S]*?)\n\s*\}/);
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
    assert.ok(formRule, 'watchPartyConfigForm styles should exist');
    assert.doesNotMatch(html, /class="readOnlyContent auto-center watch-party-page"/);
    assert.match(pageRule[1], /--wp-surface:\s*#fff(?:fff)?;/i);
    assert.match(pageRule[1], /--wp-text:\s*#[0-9a-f]{6};/i);
    assert.match(pageRule[1], /max-width:\s*none\s*!important;/);
    assert.match(pageRule[1], /width:\s*100%\s*!important;/);
    assert.match(pageRule[1], /margin:\s*0\s*!important;/);
    assert.match(pageRule[1], /color:\s*var\(--wp-text\);/);
    assert.doesNotMatch(pageRule[1], /color:\s*inherit/);
    assert.match(formRule[1], /max-width:\s*none\s*!important;/);
    assert.match(formRule[1], /width:\s*100%\s*!important;/);
    assert.ok(contrast(variable('wp-surface'), variable('wp-text')) >= 7, 'primary text should meet enhanced contrast');
    assert.ok(contrast(variable('wp-surface'), variable('wp-text-muted')) >= 4.5, 'secondary text should meet normal contrast');
    assert.doesNotMatch(html, /#202832|#17211f|#1b232d|background:\s*rgba\(24,\s*27,\s*32/);
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
