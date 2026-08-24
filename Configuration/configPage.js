define(['baseView', 'loading', 'toast', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-select'], function (BaseView, loading, toast) {
    'use strict';

    const pluginId = "a1b2c3d4-e5f6-4a5b-8c9d-0e1f2a3b4c5d";
    function finiteInteger(value, fallback) {
        const normalizedValue = typeof value === 'string' ? value.trim() : value;
        if (normalizedValue === null || normalizedValue === undefined || normalizedValue === '') {
            return fallback;
        }

        const parsed = Number(normalizedValue);
        return Number.isFinite(parsed) && Number.isInteger(parsed) ? parsed : fallback;
    }

    function getPluginConfiguration() {
        return ApiClient.getPluginConfiguration(pluginId);
    }

    function updatePluginConfiguration(config) {
        return ApiClient.updatePluginConfiguration(pluginId, config);
    }

    function loadLibraryContent(startIndex = 0, limit = 50, searchTerm = '') {
        const params = {
            Recursive: true,
            IncludeItemTypes: 'Movie,Series',
            SortBy: 'SortName,DateCreated',
            SortOrder: 'Ascending',
            Fields: 'Id,Name,ProductionYear,Type,ParentId,SeriesId,SeriesName',
            StartIndex: startIndex,
            Limit: limit
        };

        if (searchTerm) {
            params.SearchTerm = searchTerm;
        }

        return ApiClient.getItems(ApiClient.getCurrentUserId(), params);
    }

    function loadRecentlyWatched(searchTerm = '') {
        const baseParams = {
            Recursive: true,
            IncludeItemTypes: 'Movie,Episode',
            SortBy: 'DatePlayed',
            SortOrder: 'Descending',
            Fields: 'Id,Name,ProductionYear,Type,ParentId,UserData,SeriesId,SeriesName',
            Limit: 50
        };

        if (searchTerm) {
            baseParams.SearchTerm = searchTerm;
        }

        const userId = ApiClient.getCurrentUserId();
        return Promise.all([
            ApiClient.getItems(userId, Object.assign({}, baseParams, { IsResumable: true })),
            ApiClient.getItems(userId, Object.assign({}, baseParams, { IsPlayed: true }))
        ]).then(results => {
            const byId = new Map();
            results.forEach(result => (result.Items || []).forEach(item => {
                const normalized = item.Type === 'Episode'
                    ? item.SeriesId
                        ? Object.assign({}, item, {
                            Id: item.SeriesId,
                            Name: item.SeriesName || item.Name,
                            Type: 'Series',
                            ParentId: ''
                        })
                        : null
                    : item;
                if (normalized && !byId.has(String(normalized.Id))) {
                    byId.set(String(normalized.Id), normalized);
                }
            }));
            const items = Array.from(byId.values())
                .sort((left, right) => {
                    const leftDate = Date.parse(left.UserData?.LastPlayedDate || '') || 0;
                    const rightDate = Date.parse(right.UserData?.LastPlayedDate || '') || 0;
                    return rightDate - leftDate;
                })
                .slice(0, baseParams.Limit);
            return { Items: items, TotalRecordCount: items.length };
        });
    }

    function toContentOption(item) {
        const year = item.ProductionYear ? ` (${item.ProductionYear})` : '';
        const type = item.Type === 'Series' ? ' [电视剧]' : ' [电影]';
        return {
            id: item.Id,
            text: `${item.Name}${year}${type}`,
            type: item.Type,
            name: item.Name,
            libraryId: item.ParentId || ''
        };
    }

    function loadItemDetails(itemId) {
        const userId = ApiClient.getCurrentUserId();
        const url = ApiClient.getUrl(
            `Users/${encodeURIComponent(userId)}/Items/${encodeURIComponent(itemId)}`,
            { Fields: 'Id,Name,Type,MediaSources,ParentId,SeriesId,SeriesName' });
        return ApiClient.getJSON(url);
    }

    function playbackItemIdFromSource(source, fallbackItemId) {
        const sourceItemId = source?.ItemId || source?.itemId;
        if (sourceItemId) {
            return String(sourceItemId);
        }
        return fallbackItemId;
    }

    function toMediaVersionOption(source, item) {
        const sourceId = source?.Id || source?.id || '';
        const playbackItemId = playbackItemIdFromSource(source, item.Id);
        const path = source?.Path || source?.Name || '';
        const label = source?.Name || (path ? path.split(/[\\/]/).pop() : '') || '默认版本';
        return {
            optionValue: sourceId || playbackItemId,
            playbackItemId,
            mediaSourceId: sourceId || '',
            text: label,
            name: label
        };
    }

    function loadSeasons(seriesId, startIndex = 0, limit = 100, searchTerm = '') {
        const params = {
            userId: ApiClient.getCurrentUserId(),
            Fields: 'Id,Name,IndexNumber',
            StartIndex: startIndex,
            Limit: limit
        };

        if (searchTerm) {
            params.SearchTerm = searchTerm;
        }

        return ApiClient.getSeasons(seriesId, params);
    }

    function loadEpisodes(seriesId, seasonId, startIndex = 0, limit = 100, searchTerm = '') {
        const params = {
            seasonId: seasonId,
            userId: ApiClient.getCurrentUserId(),
            Fields: 'Id,Name,IndexNumber,ParentIndexNumber',
            StartIndex: startIndex,
            Limit: limit
        };

        if (searchTerm) {
            params.SearchTerm = searchTerm;
        }

        return ApiClient.getEpisodes(seriesId, params);
    }

    function loadAllSeriesEpisodes(seriesId) {
        return ApiClient.getEpisodes(seriesId, {
            userId: ApiClient.getCurrentUserId(),
            Fields: 'Id,Name,IndexNumber,ParentIndexNumber,SeasonId',
            StartIndex: 0,
            Limit: 10000
        });
    }

    function loadUsers() {
        return ApiClient.getUsers();
    }

    function resolveRoomMasterUserId(availableUserIds, configuredDefaultMasterUserId) {
        if (availableUserIds.has(configuredDefaultMasterUserId)) {
            return configuredDefaultMasterUserId;
        }

        const currentUserId = ApiClient.getCurrentUserId();
        return availableUserIds.has(currentUserId) ? currentUserId : '';
    }

    function populateUsersDropdown(
        view,
        select,
        selectedUserIds = [],
        usersPromise = null,
        options = {}) {
        return (usersPromise || loadUsers()).then(users => {
            select.innerHTML = '';
            const resolvedUserIds = typeof options.selectedUserIdsResolver === 'function'
                ? options.selectedUserIdsResolver(users)
                : selectedUserIds;
            const availableUserIds = new Set(users.map(user => user.Id));
            const effectiveSelectedUserIds = resolvedUserIds.filter(userId => availableUserIds.has(userId));

            if (options.emptyOptionLabel) {
                const emptyOption = document.createElement('option');
                emptyOption.value = '';
                emptyOption.textContent = options.emptyOptionLabel;
                emptyOption.selected = effectiveSelectedUserIds.length === 0;
                select.appendChild(emptyOption);
            }

            users.forEach(user => {
                const option = document.createElement('option');
                option.value = user.Id;
                option.textContent = user.Name;
                option.selected = effectiveSelectedUserIds.includes(user.Id);
                select.appendChild(option);
            });

            if (effectiveSelectedUserIds.length > 0) {
                select.value = effectiveSelectedUserIds[0];
            } else {
                select.value = '';
                select.selectedIndex = options.emptyOptionLabel ? 0 : -1;
            }

            if (options.rememberSelectionAsDefault) {
                select.dataset.defaultUserId = effectiveSelectedUserIds[0] || '';
            }

            return users;
        }).catch(error => {
            console.error('加载用户失败：', error);
            select.innerHTML = '<option value="">用户加载失败</option>';
            select.value = '';
            if (options.rememberSelectionAsDefault) {
                select.dataset.defaultUserId = '';
            }
            return [];
        });
    }

    return class extends BaseView {
        constructor(view, params) {
            super(view, params);

            const form = view.querySelector('#watchPartyConfigForm');
            form.addEventListener('submit', (e) => {
                e.preventDefault();
                this.saveData(view);
                return false;
            });

            ['input', 'change'].forEach(eventName => {
                form.addEventListener(eventName, () => this.updateRoomDraftSummary(view));
            });

            view.querySelector('#btnSaveSettings').addEventListener('click', () => {
                this.saveGlobalSettings(view);
            });

            view.querySelector('#btnOpenDashboard').addEventListener('click', () => {
                const currentSettings = Object.assign({}, this.config || {}, {
                    ExternalWebServerPort: finiteInteger(
                        view.querySelector('#externalWebServerPort').value,
                        8097),
                    EnableHttps: view.querySelector('#enableHttps').checked,
                    UseReverseProxy: view.querySelector('#useReverseProxy').checked,
                    ExternalServerUrl: view.querySelector('#externalServerUrl').value.trim(),
                    EmbyServerUrl: view.querySelector('#embyServerUrl').value.trim(),
                    ListenAddress: view.querySelector('#listenAddress').value.trim()
                });
                const dashboardUrl = this.getDashboardUrl(currentSettings);
                if (!dashboardUrl) {
                    toast({
                        type: 'error',
                        text: '反向代理模式无法自动推断公开控制台地址，请使用你在代理中配置的地址。'
                    });
                    return;
                }
                window.open(dashboardUrl, '_blank');
            });

            view.querySelector('#contentSourceMode').addEventListener('change', (e) => {
                this.onContentSourceChange(view, e.target.value);
            });

            view.querySelector('#mediaVersionId').addEventListener('change', (e) => {
                const option = e.target.selectedOptions?.[0];
                view.querySelector('#selectedMediaSourceId').value = option?.dataset?.sourceId || '';
                view.querySelector('#selectedMediaVersionItemId').value = option?.dataset?.itemId || '';
                this.updateRoomDraftSummary(view);
            });

            this.setupAutocomplete(view, 'searchContent', 'selectedItemId', 'searchContentDropdown',
                (itemData) => {
                    this.onItemSelect(view, itemData);
                    this.updateRoomDraftSummary(view);
                },
                () => this.loadMoreLibraryContent(view)
            );

            this.setupAutocomplete(view, 'searchSeason', 'selectedSeasonId', 'searchSeasonDropdown',
                (itemData) => this.onSeasonSelect(view, itemData),
                () => this.loadMoreSeasons(view)
            );

            this.setupAutocomplete(view, 'searchEpisode', 'selectedEpisodeId', 'searchEpisodeDropdown',
                (itemData) => this.onEpisodeSelect(view, itemData),
                () => this.loadMoreEpisodes(view)
            );

            view.querySelector('#isSeriesParty').addEventListener('change', (e) => {
                this.updateSeriesPartyMode(view, e.target.checked);
            });

            view.querySelector('#autoStartWhenReady').addEventListener('change', () => {
                this.updateDependentFields(view);
            });

            view.querySelector('#autoKickInactive').addEventListener('change', () => {
                this.updateDependentFields(view);
            });

            view.querySelector('#useReverseProxy').addEventListener('change', () => {
                this.updateDependentFields(view);
            });

            view.querySelector('#isWaitingRoom').addEventListener('change', () => {
                this.updateDependentFields(view);
            });

            ['enableHttps', 'enableSecurityHeaders', 'enableHsts', 'enableAccountLockout']
                .forEach(id => view.querySelector(`#${id}`).addEventListener('change', () => {
                    this.updateDependentFields(view);
                }));

            ['allowedUsers', 'masterUser'].forEach(id => {
                view.querySelector(`#${id}`).addEventListener('change', () => {
                    this.ensureMasterInWhitelistSelection(view);
                });
            });

            view.querySelector('#defaultMasterUser').addEventListener('change', () => {
                this.applyDefaultMasterToRoomDraft(view);
            });

            view.querySelector('#externalWebServerPort').addEventListener('input', (e) => {
                const port = e.target.value || '8097';
                const placeholders = view.querySelectorAll('#portPlaceholder, #portPlaceholderDocker, #portPlaceholderDocker2');
                placeholders.forEach(el => el.textContent = port);
            });
        }

        setupAutocomplete(view, searchInputId, hiddenInputId, dropdownId, onSelectCallback, onLoadMoreCallback) {
            const searchInput = view.querySelector(`#${searchInputId}`);
            const hiddenInput = view.querySelector(`#${hiddenInputId}`);
            const dropdown = view.querySelector(`#${dropdownId}`);

            let searchTimeout;

            searchInput.addEventListener('focus', () => {
                if (this[`${searchInputId}_items`] && this[`${searchInputId}_items`].length > 0) {
                    this.renderDropdown(view, searchInputId, hiddenInputId, dropdownId, searchInput.value, onSelectCallback, onLoadMoreCallback);
                }
            });

            searchInput.addEventListener('input', (e) => {
                const query = e.target.value.trim();
                hiddenInput.value = '';
                hiddenInput.dataset.type = '';
                hiddenInput.dataset.name = '';

                clearTimeout(searchTimeout);

                if (query.length >= 2) {
                    searchTimeout = setTimeout(() => {
                        this.performSearch(view, searchInputId, query, onSelectCallback, onLoadMoreCallback);
                    }, 300);
                } else if (query.length === 0) {
                    if (this[`${searchInputId}_items`] && this[`${searchInputId}_items`].length > 0) {
                        this.renderDropdown(view, searchInputId, hiddenInputId, dropdownId, query, onSelectCallback, onLoadMoreCallback);
                    } else {
                        this.setAutocompleteOpen(searchInput, dropdown, false);
                    }
                } else {
                    this.setAutocompleteOpen(searchInput, dropdown, false);
                }
            });

            searchInput.addEventListener('keydown', (event) => {
                if (event.key === 'Escape') {
                    this.setAutocompleteOpen(searchInput, dropdown, false);
                    event.preventDefault();
                    return;
                }

                if (!['ArrowDown', 'ArrowUp', 'Enter'].includes(event.key)) {
                    return;
                }

                if (!dropdown.classList.contains('show')
                    && this[`${searchInputId}_items`]
                    && this[`${searchInputId}_items`].length > 0) {
                    this.renderDropdown(view, searchInputId, hiddenInputId, dropdownId, searchInput.value, onSelectCallback, onLoadMoreCallback);
                }

                const options = Array.from(dropdown.querySelectorAll('[role="option"]'));
                if (options.length === 0) {
                    if (event.key === 'Enter') {
                        event.preventDefault();
                    }
                    return;
                }

                let activeIndex = this[`${searchInputId}_activeIndex`] ?? -1;
                if (event.key === 'ArrowDown') {
                    activeIndex = (activeIndex + 1) % options.length;
                    this.setActiveAutocompleteOption(searchInput, options, activeIndex);
                } else if (event.key === 'ArrowUp') {
                    activeIndex = activeIndex <= 0 ? options.length - 1 : activeIndex - 1;
                    this.setActiveAutocompleteOption(searchInput, options, activeIndex);
                } else if (activeIndex >= 0 && options[activeIndex]) {
                    options[activeIndex].click();
                } else {
                    if (event.key === 'Enter') {
                        event.preventDefault();
                    }
                    return;
                }

                this[`${searchInputId}_activeIndex`] = activeIndex;
                event.preventDefault();
            });

            document.addEventListener('click', (e) => {
                if (!searchInput.contains(e.target) && !dropdown.contains(e.target)) {
                    this.setAutocompleteOpen(searchInput, dropdown, false);
                }
            });
        }

        setAutocompleteOpen(searchInput, dropdown, isOpen) {
            if (isOpen) {
                dropdown.classList.add('show');
            } else {
                dropdown.classList.remove('show');
                searchInput.removeAttribute('aria-activedescendant');
            }

            searchInput.setAttribute('aria-expanded', String(isOpen));
        }

        setActiveAutocompleteOption(searchInput, options, activeIndex) {
            options.forEach((option, index) => {
                const isActive = index === activeIndex;
                option.classList.toggle('selected', isActive);
                option.setAttribute('aria-selected', String(isActive));
            });

            const activeOption = options[activeIndex];
            if (activeOption) {
                searchInput.setAttribute('aria-activedescendant', activeOption.id);
                if (typeof activeOption.scrollIntoView === 'function') {
                    activeOption.scrollIntoView({ block: 'nearest' });
                }
            }
        }

        renderDropdown(view, searchInputId, hiddenInputId, dropdownId, query, onSelectCallback, onLoadMoreCallback) {
            const searchInput = view.querySelector(`#${searchInputId}`);
            const hiddenInput = view.querySelector(`#${hiddenInputId}`);
            const dropdown = view.querySelector(`#${dropdownId}`);
            const items = this[`${searchInputId}_items`] || [];
            const metadata = this[`${searchInputId}_metadata`] || {};

            const lowerQuery = query.toLowerCase().trim();
            const filteredItems = items.filter(item => {
                if (!lowerQuery) return true;
                return item.text.toLowerCase().includes(lowerQuery);
            });

            this[`${searchInputId}_activeIndex`] = -1;
            searchInput.removeAttribute('aria-activedescendant');
            dropdown.innerHTML = '';

            if (filteredItems.length === 0 && !metadata.hasMore) {
                const noResults = document.createElement('div');
                noResults.className = 'autocomplete-item';
                noResults.textContent = '没有找到结果';
                noResults.setAttribute('role', 'status');
                noResults.style.textAlign = 'center';
                noResults.style.color = '#999';
                dropdown.appendChild(noResults);
                this.setAutocompleteOpen(searchInput, dropdown, true);
                return;
            }

            filteredItems.forEach((item, index) => {
                const div = document.createElement('div');
                div.className = 'autocomplete-item';
                div.textContent = item.text;
                div.dataset.value = item.id;
                div.id = `${dropdownId}-option-${index}`;
                div.setAttribute('role', 'option');
                div.setAttribute('aria-selected', 'false');

                div.addEventListener('click', () => {
                    searchInput.value = item.text;
                    hiddenInput.value = item.id;
                    hiddenInput.dataset.type = item.type || '';
                    hiddenInput.dataset.name = item.name || item.text;
                    hiddenInput.dataset.libraryId = item.libraryId || '';
                    this.setAutocompleteOpen(searchInput, dropdown, false);

                    if (onSelectCallback) {
                        onSelectCallback({
                            id: item.id,
                            text: item.text,
                            type: item.type,
                            name: item.name || item.text,
                            libraryId: item.libraryId || ''
                        });
                    }
                });

                dropdown.appendChild(div);
            });

            if (metadata.hasMore && !lowerQuery) {
                const loadMore = document.createElement('div');
                loadMore.className = 'autocomplete-item load-more';
                loadMore.textContent = `--- 加载更多（已加载 ${metadata.currentCount}/${metadata.totalCount}）---`;
                loadMore.id = `${dropdownId}-option-load-more`;
                loadMore.setAttribute('role', 'option');
                loadMore.setAttribute('aria-selected', 'false');
                loadMore.dataset.action = 'load-more';
                loadMore.addEventListener('click', () => {
                    if (onLoadMoreCallback) {
                        onLoadMoreCallback();
                    }
                });
                dropdown.appendChild(loadMore);
            }

            this.setAutocompleteOpen(searchInput, dropdown, true);
        }

        performSearch(view, searchInputId, query, onSelectCallback, onLoadMoreCallback) {
            const dropdown = view.querySelector(`#${searchInputId}Dropdown`);

            if (searchInputId === 'searchContent' && this.contentSourceMode === 'recent') {
                this.loadRecentContent(view, query);
            } else if (searchInputId === 'searchContent' && this.contentSourceMode === 'library') {
                const requestVersion = this.libraryRequestVersion || 0;
                this[`${searchInputId}_searchMode`] = true;
                loading.show();
                loadLibraryContent(0, 50, query).then(result => {
                    if (this.contentSourceMode !== 'library'
                        || (this.libraryRequestVersion || 0) !== requestVersion) {
                        return;
                    }
                    const items = result.Items || [];
                    const totalCount = result.TotalRecordCount || items.length;

                    this[`${searchInputId}_items`] = items.map(toContentOption);

                    this[`${searchInputId}_metadata`] = {
                        hasMore: false,
                        currentCount: items.length,
                        totalCount: totalCount
                    };

                    this.renderDropdown(view, searchInputId, `${searchInputId === 'searchContent' ? 'selectedItemId' : searchInputId === 'searchSeason' ? 'selectedSeasonId' : 'selectedEpisodeId'}`, `${searchInputId}Dropdown`, '', onSelectCallback, onLoadMoreCallback);
                    loading.hide();
                }).catch(() => {
                    if (this.contentSourceMode !== 'library'
                        || (this.libraryRequestVersion || 0) !== requestVersion) {
                        return;
                    }
                    loading.hide();
                    toast({ type: 'error', text: '搜索内容失败。' });
                });
            } else if (searchInputId === 'searchSeason' && this.currentSeriesId) {
                this[`${searchInputId}_searchMode`] = true;
                loading.show();
                loadSeasons(this.currentSeriesId, 0, 50, query).then(result => {
                    const seasons = result.Items || [];
                    const totalCount = result.TotalRecordCount || seasons.length;

                    this[`${searchInputId}_items`] = seasons.map(season => {
                        const seasonNum = season.IndexNumber ? ` ${season.IndexNumber}` : '';
                        return {
                            id: season.Id,
                            text: `${season.Name || '第' + seasonNum + '季'}`,
                            name: season.Name || '第' + seasonNum + '季'
                        };
                    });

                    this[`${searchInputId}_metadata`] = {
                        hasMore: false,
                        currentCount: seasons.length,
                        totalCount: totalCount
                    };

                    this.renderDropdown(view, searchInputId, 'selectedSeasonId', `${searchInputId}Dropdown`, '', onSelectCallback, onLoadMoreCallback);
                    loading.hide();
                }).catch(() => {
                    loading.hide();
                    toast({ type: 'error', text: '搜索季失败。' });
                });
            } else if (searchInputId === 'searchEpisode' && this.currentSeasonId) {
                const seriesId = view.querySelector('#selectedItemId').value;
                this[`${searchInputId}_searchMode`] = true;
                loading.show();
                loadEpisodes(seriesId, this.currentSeasonId, 0, 50, query).then(result => {
                    const episodes = result.Items || [];
                    const totalCount = result.TotalRecordCount || episodes.length;

                    this[`${searchInputId}_items`] = episodes.map(episode => {
                        const epNum = episode.IndexNumber ? `E${episode.IndexNumber}` : '';
                        const seasonNum = episode.ParentIndexNumber ? `S${episode.ParentIndexNumber}` : '';
                        return {
                            id: episode.Id,
                            text: `${seasonNum}${epNum} - ${episode.Name}`,
                            name: episode.Name
                        };
                    });

                    this[`${searchInputId}_metadata`] = {
                        hasMore: false,
                        currentCount: episodes.length,
                        totalCount: totalCount
                    };

                    this.renderDropdown(view, searchInputId, 'selectedEpisodeId', `${searchInputId}Dropdown`, '', onSelectCallback, onLoadMoreCallback);
                    loading.hide();
                }).catch(() => {
                    loading.hide();
                    toast({ type: 'error', text: '搜索剧集失败。' });
                });
            }
        }

        onContentSourceChange(view, source) {
            this.contentSourceMode = source === 'library' ? 'library' : 'recent';
            this.recentContentRequestVersion = (this.recentContentRequestVersion || 0) + 1;
            this.libraryRequestVersion = (this.libraryRequestVersion || 0) + 1;
            this.currentSeriesId = null;
            this.currentSeasonId = null;
            this.searchContent_items = [];
            this.searchContent_metadata = { hasMore: false, currentCount: 0, totalCount: 0 };
            const itemInput = view.querySelector('#selectedItemId');
            itemInput.value = '';
            itemInput.dataset.type = '';
            itemInput.dataset.name = '';
            itemInput.dataset.libraryId = '';
            view.querySelector('#searchContent').value = '';
            this.hideSeriesControls(view);

            if (this.contentSourceMode === 'recent') {
                this.loadRecentContent(view);
            }
        }

        loadRecentContent(view, searchTerm = '') {
            loading.show();
            if (this.contentSourceMode !== 'library') {
                this.contentSourceMode = 'recent';
            }
            const requestVersion = (this.recentContentRequestVersion || 0) + 1;
            this.recentContentRequestVersion = requestVersion;

            return loadRecentlyWatched(searchTerm).then(result => {
                if (this.contentSourceMode !== 'recent'
                    || this.recentContentRequestVersion !== requestVersion) {
                    return [];
                }
                const items = result.Items || [];
                const totalCount = result.TotalRecordCount || items.length;
                this.contentSourceMode = 'recent';
                this.searchContent_items = items.map(toContentOption);
                this.searchContent_metadata = {
                    hasMore: false,
                    currentCount: items.length,
                    totalCount
                };
                this.libraryContentOffset = items.length;
                if (searchTerm) {
                    this.renderDropdown(
                        view,
                        'searchContent',
                        'selectedItemId',
                        'searchContentDropdown',
                        searchTerm,
                        itemData => this.onItemSelect(view, itemData),
                        null);
                }
                loading.hide();
                return items;
            }).catch(() => {
                if (this.contentSourceMode !== 'recent'
                    || this.recentContentRequestVersion !== requestVersion) {
                    return [];
                }
                loading.hide();
                toast({ type: 'error', text: '加载最近观看失败。' });
                return [];
            });
        }

        resetMediaVersionSelection(view, itemId = '') {
            const sourceInput = view.querySelector('#selectedMediaSourceId');
            const versionItemInput = view.querySelector('#selectedMediaVersionItemId');
            const versionSelect = view.querySelector('#mediaVersionId');
            const versionContainer = view.querySelector('#mediaVersionContainer');
            if (sourceInput) sourceInput.value = '';
            if (versionItemInput) versionItemInput.value = itemId || '';
            if (versionSelect) {
                versionSelect.innerHTML = '<option value="">正在读取具体版本……</option>';
                versionSelect.value = '';
            }
            if (versionContainer) versionContainer.style.display = 'none';
        }

        loadMediaVersions(view, itemId) {
            this.mediaVersionRequestVersion = (this.mediaVersionRequestVersion || 0) + 1;
            const requestVersion = this.mediaVersionRequestVersion;
            this.resetMediaVersionSelection(view, itemId);
            if (!itemId) {
                return Promise.resolve([]);
            }

            loading.show();
            return loadItemDetails(itemId).then(details => {
                if (this.mediaVersionRequestVersion !== requestVersion) {
                    return [];
                }

                const sources = (details?.MediaSources || [])
                    .map(source => toMediaVersionOption(source, details))
                    .filter(option => option.optionValue || option.playbackItemId);
                const uniqueSources = Array.from(new Map(
                    sources.map(option => [
                        `${option.mediaSourceId}|${option.playbackItemId}`,
                        option
                    ])).values());
                const versionSelect = view.querySelector('#mediaVersionId');
                const versionContainer = view.querySelector('#mediaVersionContainer');
                const sourceInput = view.querySelector('#selectedMediaSourceId');
                const versionItemInput = view.querySelector('#selectedMediaVersionItemId');

                if (uniqueSources.length === 0) {
                    if (versionItemInput) versionItemInput.value = itemId;
                    if (versionContainer) versionContainer.style.display = 'none';
                    loading.hide();
                    return [];
                }

                versionSelect.innerHTML = '';
                uniqueSources.forEach((option, index) => {
                    const element = document.createElement('option');
                    element.value = option.optionValue || option.playbackItemId;
                    element.dataset.sourceId = option.mediaSourceId;
                    element.dataset.itemId = option.playbackItemId;
                    element.textContent = option.text || `版本 ${index + 1}`;
                    versionSelect.appendChild(element);
                });
                versionSelect.selectedIndex = 0;
                const selected = versionSelect.selectedOptions?.[0];
                if (sourceInput) sourceInput.value = selected?.dataset?.sourceId || '';
                if (versionItemInput) versionItemInput.value = selected?.dataset?.itemId || itemId;
                if (versionContainer) versionContainer.style.display = 'block';
                loading.hide();
                return uniqueSources;
            }).catch(() => {
                if (this.mediaVersionRequestVersion !== requestVersion) {
                    return [];
                }
                this.resetMediaVersionSelection(view, itemId);
                loading.hide();
                toast({ type: 'error', text: '读取媒体具体版本失败，将使用默认版本。' });
                return [];
            });
        }

        onItemSelect(view, itemData) {
            if (!itemData.id) {
                this.hideSeriesControls(view);
                this.resetMediaVersionSelection(view);
                return;
            }

            this.resetMediaVersionSelection(view, itemData.id);

            if (itemData.type === 'Series') {
                this.currentSeriesId = itemData.id;
                this.seasonsOffset = 0;
                this.searchSeason_searchMode = false;

                const searchSeason = view.querySelector('#searchSeason');
                const searchEpisode = view.querySelector('#searchEpisode');
                if (searchSeason) searchSeason.value = '';
                if (searchEpisode) searchEpisode.value = '';
                view.querySelector('#selectedSeasonId').value = '';
                view.querySelector('#selectedEpisodeId').value = '';

                this.showSeriesControls(view);
                loading.show();
                loadSeasons(itemData.id, 0, 100).then(result => {
                    const seasons = result.Items || [];
                    const totalCount = result.TotalRecordCount || seasons.length;

                    this.searchSeason_items = seasons.map(season => {
                        const seasonNum = season.IndexNumber ? ` ${season.IndexNumber}` : '';
                        return {
                            id: season.Id,
                            text: `${season.Name || '第' + seasonNum + '季'}`,
                            name: season.Name || '第' + seasonNum + '季'
                        };
                    });

                    this.searchSeason_metadata = {
                        hasMore: seasons.length < totalCount,
                        currentCount: seasons.length,
                        totalCount: totalCount
                    };

                    this.seasonsOffset = seasons.length;

                    loading.hide();
                }).catch(() => {
                    loading.hide();
                    toast({ type: 'error', text: '加载季失败。' });
                });
            } else {
                this.hideSeriesControls(view);
                this.loadMediaVersions(view, itemData.id);
            }
        }

        onSeasonSelect(view, itemData) {
            if (!itemData.id) {
                this.searchEpisode_searchMode = false;
                view.querySelector('#searchEpisode').value = '';
                view.querySelector('#selectedEpisodeId').value = '';
                return;
            }

            const seriesId = view.querySelector('#selectedItemId').value;
            this.currentSeasonId = itemData.id;
            this.episodesOffset = 0;
            this.searchEpisode_searchMode = false;
            this.episodesOffset = 0;

            const searchEpisode = view.querySelector('#searchEpisode');
            if (searchEpisode) searchEpisode.value = '';
            view.querySelector('#selectedEpisodeId').value = '';

            loading.show();
            loadEpisodes(seriesId, itemData.id, 0, 100).then(result => {
                const episodes = result.Items || [];
                const totalCount = result.TotalRecordCount || episodes.length;

                this.searchEpisode_items = episodes.map(episode => {
                    const epNum = episode.IndexNumber ? `E${episode.IndexNumber}` : '';
                    const seasonNum = episode.ParentIndexNumber ? `S${episode.ParentIndexNumber}` : '';
                    return {
                        id: episode.Id,
                        text: `${seasonNum}${epNum} - ${episode.Name}`,
                        name: episode.Name
                    };
                });

                this.searchEpisode_metadata = {
                    hasMore: episodes.length < totalCount,
                    currentCount: episodes.length,
                    totalCount: totalCount
                };

                this.episodesOffset = episodes.length;

                loading.hide();
            }).catch(() => {
                loading.hide();
                toast({ type: 'error', text: '加载剧集失败。' });
            });
        }

        onEpisodeSelect(view, itemData) {
            if (!itemData.id) {
                this.resetMediaVersionSelection(view);
                return;
            }

            this.loadMediaVersions(view, itemData.id);
        }

        loadMoreLibraryContent(view) {
            if (this.contentSourceMode !== 'library') return;

            loading.show();
            loadLibraryContent(this.libraryContentOffset, 100).then(result => {
                const items = result.Items || [];
                const totalCount = result.TotalRecordCount || items.length;

                const newItems = items.map(item => toContentOption(item));

                this.searchContent_items = this.searchContent_items.concat(newItems);
                this.libraryContentOffset += items.length;

                this.searchContent_metadata = {
                    hasMore: this.libraryContentOffset < totalCount,
                    currentCount: this.libraryContentOffset,
                    totalCount: totalCount
                };

                this.renderDropdown(view, 'searchContent', 'selectedItemId', 'searchContentDropdown',
                    view.querySelector('#searchContent').value,
                    (itemData) => this.onItemSelect(view, itemData),
                    () => this.loadMoreLibraryContent(view)
                );

                loading.hide();
            }).catch(() => {
                loading.hide();
                toast({ type: 'error', text: '加载更多内容失败。' });
            });
        }

        loadMoreSeasons(view) {
            if (!this.currentSeriesId) return;

            loading.show();
            loadSeasons(this.currentSeriesId, this.seasonsOffset, 100).then(result => {
                const seasons = result.Items || [];
                const totalCount = result.TotalRecordCount || seasons.length;

                const newItems = seasons.map(season => {
                    const seasonNum = season.IndexNumber ? ` ${season.IndexNumber}` : '';
                    return {
                        id: season.Id,
                        text: `${season.Name || '第' + seasonNum + '季'}`,
                        name: season.Name || '第' + seasonNum + '季'
                    };
                });

                this.searchSeason_items = this.searchSeason_items.concat(newItems);
                this.seasonsOffset += seasons.length;

                this.searchSeason_metadata = {
                    hasMore: this.seasonsOffset < totalCount,
                    currentCount: this.seasonsOffset,
                    totalCount: totalCount
                };

                this.renderDropdown(view, 'searchSeason', 'selectedSeasonId', 'searchSeasonDropdown',
                    view.querySelector('#searchSeason').value,
                    (itemData) => this.onSeasonSelect(view, itemData),
                    () => this.loadMoreSeasons(view)
                );

                loading.hide();
            }).catch(() => {
                loading.hide();
                toast({ type: 'error', text: '加载更多季失败。' });
            });
        }

        loadMoreEpisodes(view) {
            if (!this.currentSeasonId) return;

            const seriesId = view.querySelector('#selectedItemId').value;

            loading.show();
            loadEpisodes(seriesId, this.currentSeasonId, this.episodesOffset, 100).then(result => {
                const episodes = result.Items || [];
                const totalCount = result.TotalRecordCount || episodes.length;

                const newItems = episodes.map(episode => {
                    const epNum = episode.IndexNumber ? `E${episode.IndexNumber}` : '';
                    const seasonNum = episode.ParentIndexNumber ? `S${episode.ParentIndexNumber}` : '';
                    return {
                        id: episode.Id,
                        text: `${seasonNum}${epNum} - ${episode.Name}`,
                        name: episode.Name
                    };
                });

                this.searchEpisode_items = this.searchEpisode_items.concat(newItems);
                this.episodesOffset += episodes.length;

                this.searchEpisode_metadata = {
                    hasMore: this.episodesOffset < totalCount,
                    currentCount: this.episodesOffset,
                    totalCount: totalCount
                };

                this.renderDropdown(view, 'searchEpisode', 'selectedEpisodeId', 'searchEpisodeDropdown',
                    view.querySelector('#searchEpisode').value,
                    (itemData) => this.onEpisodeSelect(view, itemData),
                    () => this.loadMoreEpisodes(view)
                );

                loading.hide();
            }).catch(() => {
                loading.hide();
                toast({ type: 'error', text: '加载更多剧集失败。' });
            });
        }

        showSeriesControls(view) {
            view.querySelector('#seriesPartyContainer').style.display = 'block';
            view.querySelector('#seriesContainer').style.display = 'block';
            view.querySelector('#episodeContainer').style.display = 'block';
            this.updateSeriesPartyMode(view, view.querySelector('#isSeriesParty').checked);
        }

        hideSeriesControls(view) {
            view.querySelector('#seriesPartyContainer').style.display = 'none';
            view.querySelector('#seriesContainer').style.display = 'none';
            view.querySelector('#episodeContainer').style.display = 'none';
            view.querySelector('#isSeriesParty').checked = false;
            this.updateSeriesPartyMode(view, false);

            this.searchSeason_items = [];
            this.searchEpisode_items = [];

            view.querySelector('#searchSeason').value = '';
            view.querySelector('#selectedSeasonId').value = '';
            view.querySelector('#searchEpisode').value = '';
            view.querySelector('#selectedEpisodeId').value = '';

            this.setAutocompleteOpen(
                view.querySelector('#searchSeason'),
                view.querySelector('#searchSeasonDropdown'),
                false
            );
            this.setAutocompleteOpen(
                view.querySelector('#searchEpisode'),
                view.querySelector('#searchEpisodeDropdown'),
                false
            );
        }

        updateSeriesPartyMode(view, isSeriesParty) {
            const seasonDescription = view.querySelector('#seasonFieldDescription');
            const episodeLabel = view.querySelector('#episodeFieldLabel');
            const episodeDescription = view.querySelector('#episodeFieldDescription');
            const modeHint = view.querySelector('#seriesPartyModeHint');

            if (isSeriesParty) {
                seasonDescription.textContent = '可选：从所选季的第一集开始。';
                episodeLabel.textContent = '起播集（可选）';
                episodeDescription.textContent = '可选：指定准确的起播集；季和集都留空则从整部剧的第一集开始。';
                modeHint.textContent = '整部剧集模式：所有季的常规剧集都会按顺序加入队列；如果选择了起播集的具体版本，只对该起播集生效，后续集使用各自默认版本。';
            } else {
                seasonDescription.textContent = '单集房间必须选择季。';
                episodeLabel.textContent = '选择集';
                episodeDescription.textContent = '必选：请选择该房间要观看的单集。';
                modeHint.textContent = '单集模式：请选择季和集；如需完整有序队列，请开启整部剧集房间。';
            }
        }

        setContainerVisible(view, id, isVisible) {
            const element = view.querySelector(`#${id}`);
            if (element) {
                element.style.display = isVisible ? 'block' : 'none';
            }
        }

        updateDependentFields(view) {
            const waitingRoomEnabled = view.querySelector('#isWaitingRoom').checked;
            const autoStartEnabled = waitingRoomEnabled && view.querySelector('#autoStartWhenReady').checked;
            this.setContainerVisible(view, 'autoStartWhenReadyContainer', waitingRoomEnabled);
            this.setContainerVisible(view, 'minReadyCountContainer', autoStartEnabled);
            view.querySelector('#autoStartWhenReady').disabled = !waitingRoomEnabled;
            view.querySelector('#minReadyCount').disabled = !autoStartEnabled;

            const autoKickEnabled = view.querySelector('#autoKickInactive').checked;
            this.setContainerVisible(view, 'inactiveTimeoutContainer', autoKickEnabled);
            view.querySelector('#inactiveTimeoutMinutes').disabled = !autoKickEnabled;

            const useReverseProxy = view.querySelector('#useReverseProxy').checked;
            const httpsEnabled = !useReverseProxy && view.querySelector('#enableHttps').checked;
            const securityHeadersEnabled = !useReverseProxy && view.querySelector('#enableSecurityHeaders').checked;
            const hstsEnabled = httpsEnabled && view.querySelector('#enableHsts').checked;

            this.setContainerVisible(view, 'corsOriginsContainer', !useReverseProxy);
            this.setContainerVisible(view, 'httpsContainer', !useReverseProxy);
            this.setContainerVisible(view, 'certThumbprintContainer', httpsEnabled);
            this.setContainerVisible(view, 'securityHeadersContainer', !useReverseProxy);
            this.setContainerVisible(view, 'cspContainer', securityHeadersEnabled);
            this.setContainerVisible(view, 'hstsContainer', httpsEnabled);
            this.setContainerVisible(view, 'hstsMaxAgeContainer', hstsEnabled);
            view.querySelector('#hstsMaxAge').disabled = !hstsEnabled;

            const accountLockoutEnabled = view.querySelector('#enableAccountLockout').checked;
            this.setContainerVisible(view, 'maxFailedLoginAttemptsContainer', accountLockoutEnabled);
            this.setContainerVisible(view, 'lockoutDurationContainer', accountLockoutEnabled);
            this.setContainerVisible(view, 'lockoutWindowContainer', accountLockoutEnabled);
            ['maxFailedLoginAttempts', 'lockoutDurationMinutes', 'lockoutWindowMinutes']
                .forEach(id => {
                    view.querySelector(`#${id}`).disabled = !accountLockoutEnabled;
                });
        }

        updateRoomDraftSummary(view) {
            const itemInput = view.querySelector('#selectedItemId');
            const contentName = (itemInput.dataset.name
                || view.querySelector('#searchContent').value
                || '尚未选择内容').trim();
            const itemType = itemInput.dataset.type || '';
            const mode = itemType === 'Series'
                ? (view.querySelector('#isSeriesParty').checked ? '整部剧集' : '单集')
                : itemType === 'Movie' ? '电影' : '原媒体';

            const masterSelect = view.querySelector('#masterUser');
            const selectedMaster = Array.from(masterSelect.selectedOptions || [])[0];
            const masterName = selectedMaster
                ? (selectedMaster.textContent || selectedMaster.text || selectedMaster.value)
                : (masterSelect.value || '尚未选择');
            const maxParticipants = finiteInteger(view.querySelector('#maxParticipants').value, 50);
            const activation = view.querySelector('#isPartyActive').checked
                ? '创建后立即启用'
                : '创建后停用';
            const waitingRoom = view.querySelector('#isWaitingRoom').checked ? ' · 等候室' : '';
            const mediaVersion = view.querySelector('#selectedMediaSourceId')?.value
                ? ' · 已选具体版本'
                : '';

            view.querySelector('#roomDraftSummary').textContent =
                `${contentName}${mediaVersion} · ${mode} · Master：${masterName} · ${maxParticipants} 人 · ${activation}${waitingRoom}`;
        }

        setStatusElement(view, selector, text, tone) {
            const status = view.querySelector(selector);
            status.textContent = text;
            status.dataset.tone = tone || 'neutral';
        }

        validateGlobalSettings(view) {
            const numericFields = [
                ['syncIntervalSeconds', '同步检查间隔'],
                ['syncOffsetMilliseconds', '恢复播放偏移'],
                ['externalWebServerPort', '控制台端口'],
                ['sessionExpirationMinutes', '登录会话有效期'],
                ['rateLimitRequestsPerMinute', '请求频率限制'],
                ['rateLimitBlockDurationMinutes', '超限封禁时长'],
                ['hstsMaxAge', 'HSTS 有效期'],
                ['maxFailedLoginAttempts', '最大登录失败次数'],
                ['lockoutDurationMinutes', '锁定时长'],
                ['lockoutWindowMinutes', '失败统计窗口'],
                ['maxAuditLogEntries', '审计日志最大条数']
            ];

            for (const [id, label] of numericFields) {
                const input = view.querySelector(`#${id}`);
                if (input.disabled) continue;

                const rawValue = String(input.value || '').trim();
                if (!rawValue) continue;

                const value = Number(rawValue);
                const min = Number(input.min);
                const max = Number(input.max);
                if (!Number.isInteger(value) || value < min || value > max) {
                    return `${label}必须在 ${min} 到 ${max} 之间。`;
                }
            }

            return null;
        }

        toggleReverseProxySettings(view) {
            this.updateDependentFields(view);
        }

        ensureMasterInWhitelistSelection(view) {
            const allowedUsersSelect = view.querySelector('#allowedUsers');
            const masterUserId = view.querySelector('#masterUser').value;
            const options = Array.from(allowedUsersSelect.options || []);
            const hasWhitelist = options.some(option => option.selected && option.value);

            if (!masterUserId || !hasWhitelist) {
                return;
            }

            const masterOption = options.find(option => option.value === masterUserId);
            if (masterOption) {
                masterOption.selected = true;
            }
        }

        applyDefaultMasterToRoomDraft(view) {
            const defaultMasterUserId = view.querySelector('#defaultMasterUser').value;
            const masterSelect = view.querySelector('#masterUser');
            const masterOptions = Array.from(masterSelect.options || []);
            const userOptions = masterOptions.filter(option => option.value);
            const availableUserIds = new Set(userOptions.map(option => option.value));
            const resolvedUserId = resolveRoomMasterUserId(
                availableUserIds,
                defaultMasterUserId);

            masterOptions.forEach(option => {
                option.selected = option.value === resolvedUserId;
            });
            masterSelect.value = resolvedUserId;
            masterSelect.dataset.defaultUserId = resolvedUserId;
            this.ensureMasterInWhitelistSelection(view);
            this.updateRoomDraftSummary(view);
        }

        resetCreatePartyForm(view) {
            ['searchContent', 'searchSeason', 'searchEpisode'].forEach(id => {
                view.querySelector(`#${id}`).value = '';
            });

            ['selectedItemId', 'selectedSeasonId', 'selectedEpisodeId',
                'selectedMediaSourceId', 'selectedMediaVersionItemId'].forEach(id => {
                const input = view.querySelector(`#${id}`);
                input.value = '';
                input.dataset.type = '';
                input.dataset.name = '';
                input.dataset.libraryId = '';
            });

            ['searchContentDropdown', 'searchSeasonDropdown', 'searchEpisodeDropdown'].forEach(id => {
                view.querySelector(`#${id}`).innerHTML = '';
            });

            this.setAutocompleteOpen(
                view.querySelector('#searchContent'),
                view.querySelector('#searchContentDropdown'),
                false
            );

            this.currentSeriesId = null;
            this.currentSeasonId = null;
            this.searchContent_items = [];
            this.searchSeason_items = [];
            this.searchEpisode_items = [];
            this.libraryContentOffset = 0;
            this.seasonsOffset = 0;
            this.episodesOffset = 0;
            this.contentSourceMode = 'recent';
            view.querySelector('#contentSourceMode').value = 'recent';
            const versionContainer = view.querySelector('#mediaVersionContainer');
            if (versionContainer) versionContainer.style.display = 'none';
            const versionSelect = view.querySelector('#mediaVersionId');
            if (versionSelect) versionSelect.innerHTML = '';

            const allowedUsers = view.querySelector('#allowedUsers');
            Array.from(allowedUsers.options || []).forEach(option => {
                option.selected = false;
            });
            allowedUsers.selectedIndex = -1;
            const masterUser = view.querySelector('#masterUser');
            masterUser.value = masterUser.dataset.defaultUserId || '';
            view.querySelector('#partyPassword').value = '';
            view.querySelector('#isPartyActive').checked = true;
            view.querySelector('#maxParticipants').value = 50;
            view.querySelector('#isWaitingRoom').checked = true;
            view.querySelector('#autoStartWhenReady').checked = true;
            view.querySelector('#minReadyCount').value = 1;
            view.querySelector('#syncToleranceSeconds').value = 2;
            view.querySelector('#maxBufferThresholdSeconds').value = 30;
            view.querySelector('#autoKickInactive').checked = true;
            view.querySelector('#inactiveTimeoutMinutes').value = 15;

            this.hideSeriesControls(view);
            this.updateDependentFields(view);
            this.updateRoomDraftSummary(view);
        }

        escapeHtml(value) {
            const element = document.createElement('div');
            element.textContent = value == null ? '' : String(value);
            return element.innerHTML;
        }

        formatPosition(positionTicks) {
            const totalSeconds = Math.max(0, Math.floor(Number(positionTicks || 0) / 10000000));
            const hours = Math.floor(totalSeconds / 3600);
            const minutes = Math.floor((totalSeconds % 3600) / 60);
            const seconds = totalSeconds % 60;
            return hours > 0
                ? `${hours}:${String(minutes).padStart(2, '0')}:${String(seconds).padStart(2, '0')}`
                : `${minutes}:${String(seconds).padStart(2, '0')}`;
        }

        participantControlState(participant) {
            if (participant.IsDormant) {
                return { text: '已休眠', className: 'is-warning' };
            }
            if (!participant.IsOnline) {
                return { text: '离线', className: 'is-warning' };
            }
            if (participant.CanReceiveCommands) {
                return { text: '可控制', className: 'is-ready' };
            }
            if (participant.Client === 'Emby for iOS' && !participant.HasActiveWebSocket) {
                return { text: '控制连接未建立', className: 'is-warning' };
            }
            return { text: '仅在线上报', className: 'is-warning' };
        }

        selectedLaunchTargets(partyId) {
            this.selectedLaunchTargetIdsByParty =
                this.selectedLaunchTargetIdsByParty || new Map();
            const key = String(partyId || '');
            if (!this.selectedLaunchTargetIdsByParty.has(key)) {
                this.selectedLaunchTargetIdsByParty.set(key, new Set());
            }
            return this.selectedLaunchTargetIdsByParty.get(key);
        }

        updateLaunchTargetSelection(partyId, sessionId, selected) {
            const selectedIds = this.selectedLaunchTargets(partyId);
            if (selected) {
                selectedIds.add(sessionId);
            } else {
                selectedIds.delete(sessionId);
            }
            return Array.from(selectedIds);
        }

        reconcileLaunchTargetSelection(partyId, targets) {
            const selectedIds = this.selectedLaunchTargets(partyId);
            const selectableIds = new Set((targets || [])
                .filter(target => target.CanLaunch)
                .map(target => String(target.SessionId || '')));
            Array.from(selectedIds).forEach(sessionId => {
                if (!selectableIds.has(sessionId)) {
                    selectedIds.delete(sessionId);
                }
            });
        }

        renderPartyList(view, config) {
            const container = view.querySelector('#activePartiesList');
            const focusedButton = container.querySelector
                ? container.querySelector('button:focus')
                : null;
            const focusedPartyId = focusedButton?.dataset?.partyid || '';
            const focusedAction = focusedButton
                ? Array.from(focusedButton.classList || []).find(name => name.startsWith('btn')) || ''
                : '';
            const parties = config.WatchParties || [];
            const activeCount = parties.filter(party => party.IsActive).length;
            view.querySelector('#partyCount').textContent = String(parties.length);

            if (parties.length === 0) {
                container.innerHTML = `
                    <div class="watch-party-empty-state">
                        <strong>还没有一起看房间</strong>
                        完成下面四步即可创建第一个房间。
                    </div>`;
                return;
            }

            let html = `
                <div class="watch-party-list-summary">${parties.length} 个房间 · ${activeCount} 个启用</div>
                <div class="watch-party-list">`;

            parties.forEach(party => {
                const partyId = String(party.Id || '');
                const runtime = this.partyRuntimeById?.get(String(party.Id || '')) || party;
                const participants = runtime.Participants || [];
                const launchTargetsLoaded = this.launchTargetsByParty?.has(partyId) === true;
                const launchTargets = this.launchTargetsByParty?.get(partyId) || [];
                const selectedLaunchTargetIds = this.selectedLaunchTargets(partyId);
                const onlineCount = participants.filter(participant =>
                    participant.IsOnline && !participant.IsDormant).length;
                const masterOnline = runtime.MasterOnline === true
                    || participants.some(participant => participant.IsHost && participant.IsOnline);
                const playbackState = runtime.IsPlaying ? '播放中' : '已暂停';
                const participantRows = participants.map(participant => {
                    const controlState = this.participantControlState(participant);
                    const role = participant.IsHost ? 'Master' : '参与者';
                    const client = participant.Client || '未知客户端';
                    const playback = participant.IsPaused ? '暂停' : '播放';
                    return `
                        <div class="watch-party-client">
                            <div class="watch-party-client-main">
                                <div class="watch-party-client-name">${this.escapeHtml(participant.UserName || '未知用户')}</div>
                                <div class="watch-party-client-meta">${this.escapeHtml(client)} · ${role} · ${playback} · ${this.formatPosition(participant.CurrentPositionTicks)}</div>
                            </div>
                            <span class="watch-party-client-state ${controlState.className}">${controlState.text}</span>
                        </div>`;
                }).join('');
                const runtimeDetails = `
                    <div class="watch-party-runtime">
                        <div class="watch-party-runtime-summary">
                            <span>${playbackState} · ${this.formatPosition(runtime.CurrentPositionTicks)}</span>
                            <span>Master：${masterOnline ? '在线' : '离线'}</span>
                            <span>客户端：${onlineCount}/${participants.length} 在线</span>
                        </div>
                        ${participantRows
                            ? `<div class="watch-party-client-list">${participantRows}</div>`
                            : '<div class="watch-party-runtime-empty">当前还没有客户端进入这个房间。</div>'}
                    </div>`;
                const launchTargetRows = launchTargets.map(target => {
                    const sessionId = String(target.SessionId || '');
                    const encodedSessionId = encodeURIComponent(sessionId);
                    const selected = target.CanLaunch && selectedLaunchTargetIds.has(sessionId);
                    const sessionSuffix = sessionId ? sessionId.slice(-8) : '未知';
                    const device = target.DeviceName || target.Client || 'iOS 设备';
                    const statusClass = target.CanLaunch ? 'is-ready' : 'is-warning';
                    return `
                        <label class="watch-party-launch-target${target.CanLaunch ? '' : ' is-disabled'}">
                            <input type="checkbox" class="launchTargetCheckbox" data-partyid="${encodeURIComponent(partyId)}" data-sessionid="${encodedSessionId}"${selected ? ' checked' : ''}${target.CanLaunch ? '' : ' disabled'}>
                            <span class="watch-party-launch-target-main">
                                <span class="watch-party-launch-target-name">${this.escapeHtml(target.UserName || '未知用户')} · ${this.escapeHtml(device)}</span>
                                <span class="watch-party-launch-target-meta">${this.escapeHtml(target.Client || 'Emby for iOS')} · Session …${this.escapeHtml(sessionSuffix)}${target.InRoom ? ' · 已在房间' : ''}</span>
                            </span>
                            <span class="watch-party-client-state ${statusClass}">${this.escapeHtml(target.Message || (target.CanLaunch ? '可开播' : '不可开播'))}</span>
                        </label>`;
                }).join('');
                const launchTargetDetails = `
                    <div class="watch-party-launch-panel">
                        <div class="watch-party-launch-heading">
                            <div>
                                <strong>在线官方 iOS Session</strong>
                                <span>选择本次需要拉起的设备；未勾选的 Session 不会收到命令。</span>
                            </div>
                            <span class="watch-party-launch-count">${launchTargets.length}</span>
                        </div>
                        <div class="watch-party-launch-list">
                            ${launchTargetRows || `<div class="watch-party-runtime-empty">${launchTargetsLoaded
                                ? '当前没有在线的官方 iOS Session。'
                                : '正在读取在线 Session……'}</div>`}
                        </div>
                    </div>`;
                const statusText = party.IsActive ? '已启用' : '已停用';
                const statusClass = party.IsActive ? 'is-active' : 'is-inactive';
                const created = party.CreatedDate
                    ? new Date(party.CreatedDate).toLocaleDateString()
                    : '未知';
                const encodedPartyId = encodeURIComponent(String(party.Id || ''));
                const typeText = party.ItemType === 'Episode'
                    ? '剧集'
                    : party.ItemType === 'Movie'
                        ? '电影'
                        : party.ItemType === 'Series' ? '电视剧' : '其他';

                const features = [];
                if (party.IsWaitingRoom) features.push('等候室');
                if (party.AutoKickInactiveMinutes) features.push('自动移除不活跃用户');
                if (party.IsSeriesParty) {
                    const episodeCount = (party.EpisodeQueue || []).length;
                    const currentIndex = Math.max(0, party.CurrentEpisodeIndex || 0);
                    const currentEpisode = episodeCount > currentIndex ? party.EpisodeQueue[currentIndex] : null;
                    features.push(`剧集队列：${Math.min(currentIndex + 1, episodeCount)}/${episodeCount}`);
                    if (currentEpisode && currentEpisode.ItemName) {
                        features.push(`当前集：${this.escapeHtml(currentEpisode.ItemName)}`);
                    }
                }
                const featuresText = features.length > 0 ? `<br>功能：${features.join('，')}` : '';
                const queueDetails = party.IsSeriesParty && (party.EpisodeQueue || []).length > 0
                    ? `<details class="watch-party-queue">
                           <summary>查看剧集队列</summary>
                           <ol>
                               ${party.EpisodeQueue.map((episode, index) => {
                                   const marker = index === party.CurrentEpisodeIndex ? ' ← 当前集' : '';
                                   const label = `S${String(episode.SeasonNumber).padStart(2, '0')}E${String(episode.EpisodeNumber).padStart(2, '0')} — ${episode.ItemName || ''}${marker}`;
                                   const currentClass = index === party.CurrentEpisodeIndex
                                       ? ' class="watch-party-queue-current"'
                                       : '';
                                   return `<li${currentClass}>${this.escapeHtml(label)}</li>`;
                               }).join('')}
                           </ol>
                       </details>`
                    : '';

                html += `
                    <div class="watch-party-list-card">
                        <div>
                            <h3 class="watch-party-list-title">
                                ${this.escapeHtml((party.IsSeriesParty && party.SeriesName) || party.ItemName || '未命名房间')}
                                <span class="watch-party-status ${statusClass}">${statusText}</span>
                            </h3>
                            <div class="watch-party-list-meta">
                                类型：${typeText} ·
                                上限：${party.MaxParticipants || 50} 人 ·
                                创建日期：${created}${featuresText}
                            </div>
                            ${queueDetails}
                            ${runtimeDetails}
                            ${party.IsActive ? launchTargetDetails : ''}
                        </div>
                        <div class="watch-party-list-actions">
                            ${party.IsActive ? `
                            <button is="emby-button" type="button" class="button-flat btnSyncParty" data-partyid="${encodedPartyId}" title="向勾选的在线官方 iOS Session 播放房间选定的具体版本"${selectedLaunchTargetIds.size > 0 ? '' : ' disabled'}>
                                <span>一键开播</span>
                            </button>` : ''}
                            ${party.IsActive && party.IsWaitingRoom ? `
                            <button is="emby-button" type="button" class="button-flat btnStartParty" data-partyid="${encodedPartyId}">
                                <span>结束等候室并开始</span>
                            </button>` : ''}
                            <button is="emby-button" type="button" class="button-flat btnToggleParty" data-partyid="${encodedPartyId}">
                                <span>${party.IsActive ? '停用' : '启用'}</span>
                            </button>
                            <button is="emby-button" type="button" class="button-flat btnDeleteParty" data-partyid="${encodedPartyId}">
                                <span>删除</span>
                            </button>
                        </div>
                    </div>
                `;
            });

            html += '</div>';
            container.innerHTML = html;

            container.querySelectorAll('.btnDeleteParty').forEach(btn => {
                btn.addEventListener('click', (e) => {
                    const partyId = decodeURIComponent(e.target.closest('button').dataset.partyid);
                    this.deleteParty(view, partyId);
                });
            });

            container.querySelectorAll('.btnToggleParty').forEach(btn => {
                btn.addEventListener('click', (e) => {
                    const partyId = decodeURIComponent(e.target.closest('button').dataset.partyid);
                    this.toggleParty(view, partyId);
                });
            });

            container.querySelectorAll('.btnStartParty').forEach(btn => {
                btn.addEventListener('click', (e) => {
                    const partyId = decodeURIComponent(e.target.closest('button').dataset.partyid);
                    this.startParty(view, partyId);
                });
            });

            container.querySelectorAll('.btnSyncParty').forEach(btn => {
                btn.addEventListener('click', (e) => {
                    const partyId = decodeURIComponent(e.target.closest('button').dataset.partyid);
                    const sessionIds = Array.from(this.selectedLaunchTargets(partyId));
                    this.syncParty(view, partyId, sessionIds);
                });
            });

            container.querySelectorAll('.launchTargetCheckbox').forEach(checkbox => {
                checkbox.addEventListener('change', (event) => {
                    const input = event.currentTarget || event.target;
                    const partyId = decodeURIComponent(input.dataset.partyid);
                    const sessionId = decodeURIComponent(input.dataset.sessionid);
                    this.updateLaunchTargetSelection(partyId, sessionId, input.checked);
                    const syncButton = Array.from(container.querySelectorAll('.btnSyncParty'))
                        .find(button => decodeURIComponent(button.dataset.partyid) === partyId);
                    if (syncButton) {
                        syncButton.disabled = this.selectedLaunchTargets(partyId).size === 0;
                    }
                });
            });

            if (focusedPartyId && focusedAction && container.querySelectorAll) {
                const replacement = Array.from(container.querySelectorAll('button'))
                    .find(button => button.dataset.partyid === focusedPartyId
                        && button.classList.contains(focusedAction));
                replacement?.focus?.();
            }
        }

        startPartyRuntimeRefresh(view) {
            this.stopPartyRuntimeRefresh();
            this.partyRuntimeRefreshTimer = setInterval(() => {
                this.refreshPartyRuntimeStatus(view);
            }, 5000);
            this.partyRuntimeRefreshTimer?.unref?.();
        }

        stopPartyRuntimeRefresh() {
            if (this.partyRuntimeRefreshTimer) {
                clearInterval(this.partyRuntimeRefreshTimer);
                this.partyRuntimeRefreshTimer = null;
            }
        }

        refreshPartyRuntimeStatus(view) {
            const requestVersion = (this.partyRuntimeRequestVersion || 0) + 1;
            this.partyRuntimeRequestVersion = requestVersion;
            return ApiClient.getJSON(ApiClient.getUrl('WatchParty/List')).then(result => {
                const parties = result.Parties || [];
                return Promise.all(parties.map(party => {
                    const partyId = String(party.Id || '');
                    return ApiClient.getJSON(ApiClient.getUrl(
                        `WatchParty/${encodeURIComponent(partyId)}/LaunchTargets`))
                        .then(response => [partyId, response.Targets || []])
                        .catch(() => [partyId, []]);
                })).then(targetEntries => ({ parties, targetEntries }));
            }).then(({ parties, targetEntries }) => {
                if (this.isViewPaused || this.partyRuntimeRequestVersion !== requestVersion) {
                    return [];
                }
                this.partyRuntimeById = new Map(parties.map(party => [String(party.Id || ''), party]));
                this.launchTargetsByParty = new Map(targetEntries);
                targetEntries.forEach(([partyId, targets]) =>
                    this.reconcileLaunchTargetSelection(partyId, targets));
                const fingerprint = JSON.stringify(parties.map(party => ({
                    Id: party.Id,
                    IsPlaying: party.IsPlaying,
                    CurrentPositionTicks: party.CurrentPositionTicks,
                    MasterOnline: party.MasterOnline,
                    Participants: (party.Participants || []).map(participant => ({
                        SessionId: participant.SessionId,
                        UserName: participant.UserName,
                        Client: participant.Client,
                        IsOnline: participant.IsOnline,
                        HasActiveWebSocket: participant.HasActiveWebSocket,
                        IsDormant: participant.IsDormant,
                        CanReceiveCommands: participant.CanReceiveCommands,
                        IsPaused: participant.IsPaused,
                        IsHost: participant.IsHost,
                        CurrentPositionTicks: participant.CurrentPositionTicks
                    })),
                    LaunchTargets: this.launchTargetsByParty.get(String(party.Id || ''))
                })));
                if (this.config && fingerprint !== this.partyRuntimeFingerprint) {
                    this.partyRuntimeFingerprint = fingerprint;
                    this.renderPartyList(view, this.config);
                }
                const announcement = parties.length === 0
                    ? '当前没有一起看房间'
                    : parties.map(party => {
                        const activeClients = (party.Participants || []).filter(participant =>
                            participant.IsOnline && !participant.IsDormant).length;
                        return `${party.ItemName || party.SeriesName || '未命名房间'}：${activeClients} 台客户端在线`;
                    }).join('；');
                const status = view.querySelector('#partyRuntimeStatus');
                if (status && status.textContent !== announcement) {
                    status.textContent = announcement;
                }
                return parties;
            }).catch(() => {
                this.partyRuntimeById = this.partyRuntimeById || new Map();
                return [];
            });
        }

        syncParty(view, partyId, requestedSessionIds = null) {
            const sessionIds = requestedSessionIds
                || Array.from(this.selectedLaunchTargets(partyId));
            if (sessionIds.length === 0) {
                toast({ type: 'error', text: '请先勾选至少一个在线 iOS Session。' });
                return Promise.resolve({
                    Accepted: false,
                    Message: '请先勾选至少一个在线 iOS Session。'
                });
            }

            loading.show();
            return ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl(`WatchParty/${encodeURIComponent(partyId)}/Sync`),
                dataType: 'json',
                contentType: 'application/json',
                data: JSON.stringify({ SessionIds: sessionIds })
            }).then(result => {
                loading.hide();
                const message = result?.Message || (result?.Accepted
                    ? '已向勾选的客户端发送具体版本播放命令。'
                    : '勾选的客户端当前没有可用的播放控制连接。');
                toast({
                    type: result?.Accepted ? 'success' : 'error',
                    text: message
                });
                this.refreshPartyRuntimeStatus(view);
                return result;
            }).catch(error => {
                loading.hide();
                toast({ type: 'error', text: `一键开播失败：${error.message || error}` });
                throw error;
            });
        }

        startParty(view, partyId) {
            loading.show();
            ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl(`WatchParty/${encodeURIComponent(partyId)}/Start`)
            }).then(() => getPluginConfiguration()).then(config => {
                this.config = config;
                this.renderPartyList(view, config);
                loading.hide();
                toast({ type: 'success', text: '已结束等候室，开始播放已进入房间的客户端。' });
            }).catch((error) => {
                loading.hide();
                toast({ type: 'error', text: `开始播放失败：${error.message || error}` });
            });
        }

        deleteParty(view, partyId) {
            if (!confirm('确定要删除这个一起看房间吗？')) {
                return;
            }

            loading.show();
            getPluginConfiguration().then(config => {
                config.WatchParties = config.WatchParties.filter(p => p.Id !== partyId);

                updatePluginConfiguration(config).then(result => {
                    loading.hide();
                    Dashboard.processPluginConfigurationUpdateResult(result);
                    this.renderPartyList(view, config);
                }).catch(() => {
                    loading.hide();
                    toast({ type: 'error', text: '删除房间失败。' });
                });            }).catch(() => {
                loading.hide();
                toast({ type: 'error', text: '加载设置失败。' });            });
        }

        toggleParty(view, partyId) {
            loading.show();
            getPluginConfiguration().then(config => {
                const party = config.WatchParties.find(p => p.Id === partyId);
                if (party) {
                    party.IsActive = !party.IsActive;

                    updatePluginConfiguration(config).then(result => {
                        loading.hide();
                        Dashboard.processPluginConfigurationUpdateResult(result);
                        this.renderPartyList(view, config);
                    }).catch(() => {
                        loading.hide();
                        toast({ type: 'error', text: '更新房间失败。' });
                    });
                }
            });
        }

        loadData(view) {
            loading.show();
            const requestId = (this.dataLoadRequestId || 0) + 1;
            this.dataLoadRequestId = requestId;

            getPluginConfiguration().then(config => {
                if (this.isViewPaused || this.dataLoadRequestId !== requestId) {
                    loading.hide();
                    return;
                }
                this.config = config;
                this.contentSourceMode = 'recent';
                view.querySelector('#contentSourceMode').value = 'recent';

                view.querySelector('#isPartyActive').checked = true;
                view.querySelector('#maxParticipants').value = 50;
                view.querySelector('#syncIntervalSeconds').value = finiteInteger(config.SyncIntervalSeconds, 5);
                view.querySelector('#syncOffsetMilliseconds').value = finiteInteger(config.SyncOffsetMilliseconds, 1000);
                view.querySelector('#enableExternalWebServer').checked = config.EnableExternalWebServer === true;
                view.querySelector('#externalWebServerPort').value = finiteInteger(config.ExternalWebServerPort, 8097);
                view.querySelector('#listenAddress').value = config.ListenAddress || '127.0.0.1';
                view.querySelector('#useReverseProxy').checked = config.UseReverseProxy === true;
                view.querySelector('#allowedCorsOrigins').value = config.AllowedCorsOrigins || '';
                view.querySelector('#sessionExpirationMinutes').value = finiteInteger(config.SessionExpirationMinutes, 60);
                view.querySelector('#rateLimitRequestsPerMinute').value = finiteInteger(config.RateLimitRequestsPerMinute, 60);
                view.querySelector('#rateLimitBlockDurationMinutes').value = finiteInteger(config.RateLimitBlockDurationMinutes, 15);
                view.querySelector('#externalServerUrl').value = config.ExternalServerUrl || '';
                view.querySelector('#embyServerUrl').value = config.EmbyServerUrl || '';
                view.querySelector('#embyApiKey').value = config.EmbyApiKey || '';

                view.querySelector('#enableHttps').checked = config.EnableHttps === true;
                view.querySelector('#httpsCertificateThumbprint').value = config.HttpsCertificateThumbprint || '';
                view.querySelector('#enableCsrfProtection').checked = config.EnableCsrfProtection !== false;
                view.querySelector('#enableSecurityHeaders').checked = config.EnableSecurityHeaders !== false;
                view.querySelector('#contentSecurityPolicy').value = config.ContentSecurityPolicy || "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:;";
                view.querySelector('#enableHsts').checked = config.EnableHsts !== false;
                view.querySelector('#hstsMaxAge').value = finiteInteger(config.HstsMaxAge, 31536000);
                view.querySelector('#enableAccountLockout').checked = config.EnableAccountLockout !== false;
                view.querySelector('#maxFailedLoginAttempts').value = finiteInteger(config.MaxFailedLoginAttempts, 5);
                view.querySelector('#lockoutDurationMinutes').value = finiteInteger(config.LockoutDurationMinutes, 15);
                view.querySelector('#lockoutWindowMinutes').value = finiteInteger(config.LockoutWindowMinutes, 10);
                view.querySelector('#enableAuditLogging').checked = config.EnableAuditLogging !== false;
                view.querySelector('#maxAuditLogEntries').value = finiteInteger(config.MaxAuditLogEntries, 1000);

                const port = finiteInteger(config.ExternalWebServerPort, 8097);
                const placeholders = view.querySelectorAll('#portPlaceholder, #portPlaceholderDocker, #portPlaceholderDocker2');
                placeholders.forEach(el => el.textContent = port);

                const adminPasswordField = view.querySelector('#adminPassword');
                if (config.AdminPasswordHash) {
                    adminPasswordField.placeholder = '已设置密码；输入新密码可修改';
                } else {
                    adminPasswordField.placeholder = '尚未设置密码';
                }

                this.checkWebServerStatus(view, config);

                view.querySelector('#isWaitingRoom').checked = true;
                view.querySelector('#autoStartWhenReady').checked = true;
                view.querySelector('#autoStartWhenReady').disabled = false;
                view.querySelector('#minReadyCount').value = 1;
                view.querySelector('#syncToleranceSeconds').value = 2;
                view.querySelector('#maxBufferThresholdSeconds').value = 30;
                view.querySelector('#autoKickInactive').checked = true;
                view.querySelector('#inactiveTimeoutMinutes').value = 15;
                this.updateDependentFields(view);
                this.updateRoomDraftSummary(view);

                const usersPromise = loadUsers();
                const allowedUsersSelect = view.querySelector('#allowedUsers');
                populateUsersDropdown(view, allowedUsersSelect, [], usersPromise);

                const defaultMasterUserSelect = view.querySelector('#defaultMasterUser');
                populateUsersDropdown(
                    view,
                    defaultMasterUserSelect,
                    [config.DefaultMasterUserId || ''],
                    usersPromise,
                    { emptyOptionLabel: '未指定（使用当前登录用户）' });

                const masterUserSelect = view.querySelector('#masterUser');
                populateUsersDropdown(
                    view,
                    masterUserSelect,
                    [],
                    usersPromise,
                    {
                        emptyOptionLabel: '-- 选择主控用户 --',
                        selectedUserIdsResolver: users => {
                            const availableUserIds = new Set(users.map(user => user.Id));
                            const userId = resolveRoomMasterUserId(
                                availableUserIds,
                                config.DefaultMasterUserId);
                            return userId ? [userId] : [];
                        },
                        rememberSelectionAsDefault: true
                    })
                    .then(() => this.updateRoomDraftSummary(view));

                this.renderPartyList(view, config);
                this.refreshPartyRuntimeStatus(view);
                if (!this.isViewPaused && this.dataLoadRequestId === requestId) {
                    this.startPartyRuntimeRefresh(view);
                }
                this.loadRecentContent(view);

                loading.hide();

            }).catch(() => {
                loading.hide();
                toast({ type: 'error', text: '加载设置失败。' });
            });
        }

        saveData(view) {
            loading.show();

            const contentSourceMode = view.querySelector('#contentSourceMode').value;
            const itemInput = view.querySelector('#selectedItemId');
            const itemId = itemInput.value;
            const selectedVersionItemId = view.querySelector('#selectedMediaVersionItemId')?.value || itemId;
            const mediaSourceId = view.querySelector('#selectedMediaSourceId')?.value || '';

            if (!itemId) {
                loading.hide();
                toast({ type: 'error', text: '请选择要观看的内容。' });
                return;
            }

            const itemType = itemInput.dataset.type || null;

            let finalItemId = itemId;
            let finalItemName = view.querySelector('#searchContent').value;
            let finalItemType = itemType;
            let seriesId = null;
            let seasonId = null;
            let seriesName = null;
            let isSeriesParty = false;
            let selectedEpisodeId = null;

            if (itemType === 'Series') {
                const episodeInput = view.querySelector('#selectedEpisodeId');
                const episodeId = episodeInput.value;
                isSeriesParty = view.querySelector('#isSeriesParty').checked;
                selectedEpisodeId = episodeId || null;
                seasonId = view.querySelector('#selectedSeasonId').value || null;
                seriesId = itemId;
                seriesName = itemInput.dataset.name || view.querySelector('#searchContent').value;
                finalItemType = 'Episode';

                if (!selectedEpisodeId && !isSeriesParty) {
                    loading.hide();
                    toast({ type: 'error', text: '单集电视剧房间必须选择一集。' });
                    return;
                }

                if (selectedEpisodeId) {
                    finalItemId = selectedVersionItemId || selectedEpisodeId;
                    finalItemName = view.querySelector('#searchEpisode').value;
                }
            } else {
                finalItemId = selectedVersionItemId || itemId;
            }

            const maxParticipants = finiteInteger(view.querySelector('#maxParticipants').value, NaN);
            if (!Number.isInteger(maxParticipants) || maxParticipants < 2 || maxParticipants > 100) {
                loading.hide();
                toast({ type: 'error', text: '最多参与人数必须在 2 到 100 之间。' });
                return;
            }

            const syncTolerance = finiteInteger(view.querySelector('#syncToleranceSeconds').value, NaN);
            if (!Number.isInteger(syncTolerance) || syncTolerance < 1 || syncTolerance > 60) {
                loading.hide();
                toast({ type: 'error', text: '同步容差必须在 1 到 60 秒之间。' });
                return;
            }

            const maxBufferThreshold = finiteInteger(view.querySelector('#maxBufferThresholdSeconds').value, NaN);
            if (!Number.isInteger(maxBufferThreshold) || maxBufferThreshold < 10 || maxBufferThreshold > 120) {
                loading.hide();
                toast({ type: 'error', text: '缓冲判定阈值必须在 10 到 120 秒之间。' });
                return;
            }

            const autoKickInactive = view.querySelector('#autoKickInactive').checked;
            const inactiveTimeout = finiteInteger(view.querySelector('#inactiveTimeoutMinutes').value, 15);
            if (autoKickInactive && (inactiveTimeout < 5 || inactiveTimeout > 120)) {
                loading.hide();
                toast({ type: 'error', text: '不活跃超时必须在 5 到 120 分钟之间。' });
                return;
            }

            const masterUserId = view.querySelector('#masterUser').value;
            if (!masterUserId) {
                loading.hide();
                toast({ type: 'error', text: '请选择负责控制播放的主控用户。' });
                return;
            }

            const allowedUsersSelect = view.querySelector('#allowedUsers');
            const allowedUserIds = Array.from(new Set(
                Array.from(allowedUsersSelect.selectedOptions)
                    .map(option => option.value)
                    .filter(Boolean)
            ));
            if (allowedUserIds.length > 0 && !allowedUserIds.includes(masterUserId)) {
                allowedUserIds.push(masterUserId);
            }

            const isWaitingRoom = view.querySelector('#isWaitingRoom').checked;
            const autoStartWhenReady = isWaitingRoom && view.querySelector('#autoStartWhenReady').checked;
            const minReadyCount = finiteInteger(view.querySelector('#minReadyCount').value, 1);
            if (autoStartWhenReady && (minReadyCount < 1 || minReadyCount > maxParticipants)) {
                loading.hide();
                toast({ type: 'error', text: `最低就绪人数必须在 1 到 ${maxParticipants} 之间。` });
                return;
            }

            const partyPassword = view.querySelector('#partyPassword').value || '';

            getPluginConfiguration().then(async config => {
                const partyPasswordHash = partyPassword ? await this.hashPassword(partyPassword) : '';
                let episodeQueue = [];
                let currentEpisodeIndex = -1;

                if (isSeriesParty) {
                    const episodeResult = await loadAllSeriesEpisodes(seriesId);
                    episodeQueue = (episodeResult.Items || [])
                        .filter(episode => Number.isFinite(episode.ParentIndexNumber)
                            && episode.ParentIndexNumber > 0
                            && Number.isFinite(episode.IndexNumber))
                        .sort((left, right) => (left.ParentIndexNumber - right.ParentIndexNumber)
                            || (left.IndexNumber - right.IndexNumber)
                            || (left.Name || '').localeCompare(right.Name || ''))
                        .map(episode => ({
                            ItemId: episode.Id,
                            ItemName: episode.Name,
                            SeasonId: episode.SeasonId || '',
                            SeasonNumber: episode.ParentIndexNumber,
                            EpisodeNumber: episode.IndexNumber,
                            // A series queue keeps the selected source on the
                            // explicitly selected starting episode only. Later
                            // episodes resolve their own default source instead of
                            // accidentally reusing the first episode's file.
                            MediaSourceId: selectedEpisodeId
                                && episode.Id === selectedEpisodeId
                                ? mediaSourceId || null
                                : null
                        }));

                    if (episodeQueue.length === 0) {
                        throw new Error('这部电视剧中没有找到可加入队列的常规剧集。');
                    }

                    if (selectedEpisodeId) {
                        currentEpisodeIndex = episodeQueue.findIndex(episode => episode.ItemId === selectedEpisodeId);
                    } else if (seasonId) {
                        currentEpisodeIndex = episodeQueue.findIndex(episode => episode.SeasonId === seasonId);
                    } else {
                        currentEpisodeIndex = 0;
                    }

                    if (currentEpisodeIndex < 0) {
                        throw new Error('在常规剧集队列中找不到所选起播位置。');
                    }

                    const startingEpisode = episodeQueue[currentEpisodeIndex];
                    finalItemId = startingEpisode.ItemId;
                    finalItemName = startingEpisode.ItemName;
                    seasonId = startingEpisode.SeasonId;
                }

                const newParty = {
                    Id: this.generateGuid(),
                    LibraryId: itemInput.dataset.libraryId || '',
                    ItemId: finalItemId,
                    MediaSourceId: isSeriesParty ? null : mediaSourceId || null,
                    ItemName: finalItemName,
                    ItemType: finalItemType,
                    SeriesId: seriesId,
                    SeriesName: seriesName,
                    SeasonId: seasonId,
                    IsSeriesParty: isSeriesParty,
                    EpisodeQueue: episodeQueue,
                    CurrentEpisodeIndex: currentEpisodeIndex,
                    CurrentEpisodeId: isSeriesParty ? finalItemId : null,
                    IsActive: view.querySelector('#isPartyActive').checked,
                    CurrentPositionTicks: 0,
                    IsPlaying: false,
                    MaxParticipants: maxParticipants,
                    AllowedUserIds: allowedUserIds,
                    MasterUserId: masterUserId,
                    HostUserId: masterUserId,
                    PasswordHash: partyPasswordHash,
                    IsWaitingRoom: isWaitingRoom,
                    AutoStartWhenReady: autoStartWhenReady,
                    MinReadyCount: minReadyCount,
                    SyncToleranceSeconds: syncTolerance,
                    MaxBufferThresholdSeconds: maxBufferThreshold,
                    AutoKickInactiveMinutes: autoKickInactive,
                    InactiveTimeoutMinutes: inactiveTimeout,
                    CreatedDate: new Date().toISOString()
                };

                if (!config.WatchParties) {
                    config.WatchParties = [];
                }
                config.WatchParties.push(newParty);
                this.config = config;

                updatePluginConfiguration(config).then(result => {
                    loading.hide();
                    Dashboard.processPluginConfigurationUpdateResult(result);

                    this.resetCreatePartyForm(view);

                    this.renderPartyList(view, this.config);
                    this.setStatusElement(view, '#configSaveFeedback', '房间已创建，可在房间概览中进行控制。', 'success');
                }).catch((error) => {
                    loading.hide();
                    this.setStatusElement(view, '#configSaveFeedback', '房间创建失败，请检查填写内容后重试。', 'error');
                    toast({ type: 'error', text: `创建一起看房间失败：${error.message || error}` });
                });
            }).catch((error) => {
                loading.hide();
                this.setStatusElement(view, '#configSaveFeedback', '无法准备房间，请检查媒体和服务器状态。', 'error');
                toast({ type: 'error', text: `准备一起看房间失败：${error.message || error}` });
            });
        }

        saveGlobalSettings(view) {
            this.updateDependentFields(view);
            const validationError = this.validateGlobalSettings(view);
            if (validationError) {
                this.setStatusElement(view, '#configSaveFeedback', validationError, 'error');
                toast({ type: 'error', text: validationError });
                return;
            }

            loading.show();
            this.setStatusElement(view, '#configSaveFeedback', '正在保存设置……', 'pending');

            getPluginConfiguration().then(async config => {
                config.SyncIntervalSeconds = finiteInteger(view.querySelector('#syncIntervalSeconds').value, 5);
                config.SyncOffsetMilliseconds = finiteInteger(view.querySelector('#syncOffsetMilliseconds').value, 1000);
                config.DefaultMasterUserId = view.querySelector('#defaultMasterUser').value || '';
                config.EnableExternalWebServer = view.querySelector('#enableExternalWebServer').checked;
                config.ExternalWebServerPort = finiteInteger(view.querySelector('#externalWebServerPort').value, 8097);
                config.ListenAddress = view.querySelector('#listenAddress').value.trim() || '127.0.0.1';
                config.UseReverseProxy = view.querySelector('#useReverseProxy').checked;
                config.AllowedCorsOrigins = view.querySelector('#allowedCorsOrigins').value.trim();
                config.SessionExpirationMinutes = finiteInteger(view.querySelector('#sessionExpirationMinutes').value, 60);
                config.RateLimitRequestsPerMinute = finiteInteger(view.querySelector('#rateLimitRequestsPerMinute').value, 60);
                config.RateLimitBlockDurationMinutes = finiteInteger(view.querySelector('#rateLimitBlockDurationMinutes').value, 15);
                config.ExternalServerUrl = view.querySelector('#externalServerUrl').value.trim();
                config.EmbyServerUrl = view.querySelector('#embyServerUrl').value.trim();
                config.EmbyApiKey = view.querySelector('#embyApiKey').value.trim();

                config.EnableHttps = !config.UseReverseProxy && view.querySelector('#enableHttps').checked;
                config.HttpsCertificateThumbprint = view.querySelector('#httpsCertificateThumbprint').value.trim();
                config.EnableCsrfProtection = view.querySelector('#enableCsrfProtection').checked;
                config.EnableSecurityHeaders = !config.UseReverseProxy && view.querySelector('#enableSecurityHeaders').checked;
                config.ContentSecurityPolicy = view.querySelector('#contentSecurityPolicy').value.trim();
                config.EnableHsts = config.EnableHttps && view.querySelector('#enableHsts').checked;
                config.HstsMaxAge = finiteInteger(view.querySelector('#hstsMaxAge').value, 31536000);
                config.EnableAccountLockout = view.querySelector('#enableAccountLockout').checked;
                config.MaxFailedLoginAttempts = finiteInteger(view.querySelector('#maxFailedLoginAttempts').value, 5);
                config.LockoutDurationMinutes = finiteInteger(view.querySelector('#lockoutDurationMinutes').value, 15);
                config.LockoutWindowMinutes = finiteInteger(view.querySelector('#lockoutWindowMinutes').value, 10);
                config.EnableAuditLogging = view.querySelector('#enableAuditLogging').checked;
                config.MaxAuditLogEntries = finiteInteger(view.querySelector('#maxAuditLogEntries').value, 1000);

                const adminPassword = view.querySelector('#adminPassword').value.trim();
                if (adminPassword) {
                    config.AdminPasswordHash = await this.hashPassword(adminPassword);
                    view.querySelector('#adminPassword').value = '';
                    view.querySelector('#adminPassword').placeholder = '已设置密码；输入新密码可修改';
                }

                updatePluginConfiguration(config).then(result => {
                    loading.hide();
                    Dashboard.processPluginConfigurationUpdateResult(result);

                    this.config = config;
                    this.setStatusElement(view, '#configSaveFeedback', '设置已保存并应用。', 'success');
                    setTimeout(() => {
                        this.checkWebServerStatus(view, config);
                    }, 2000);
                }).catch(() => {
                    loading.hide();
                    this.setStatusElement(view, '#configSaveFeedback', '保存失败，请检查服务器日志后重试。', 'error');
                    toast({ type: 'error', text: '保存设置失败。' });
                });
            }).catch(() => {
                loading.hide();
                this.setStatusElement(view, '#configSaveFeedback', '无法读取当前设置，未保存任何更改。', 'error');
                toast({ type: 'error', text: '加载设置失败，未保存任何更改。' });
            });
        }

        getDashboardUrl(config) {
            config = config || {};
            if (config.UseReverseProxy === true) {
                return null;
            }

            const protocol = config.EnableHttps === true ? 'https' : 'http';
            const port = finiteInteger(config.ExternalWebServerPort, 8097);
            const addressCandidates = [
                config.ExternalServerUrl,
                config.EmbyServerUrl,
                typeof ApiClient.serverAddress === 'function' ? ApiClient.serverAddress() : null
            ];
            let host = null;
            for (const candidate of addressCandidates) {
                if (!candidate) continue;
                try {
                    host = new URL(candidate).hostname;
                    if (host) break;
                } catch (_) {
                    // Fall through to the configured listener or localhost.
                }
            }

            if (!host) {
                const listenAddress = String(config.ListenAddress || '')
                    .split(',')[0]
                    .trim();
                if (listenAddress
                    && !['*', '+', '0.0.0.0', '127.0.0.1', '::1', '[::1]']
                        .includes(listenAddress)) {
                    host = listenAddress.includes(':') && !listenAddress.startsWith('[')
                        ? `[${listenAddress}]`
                        : listenAddress;
                }
            }

            return `${protocol}://${host || 'localhost'}:${port}/`;
        }

        checkWebServerStatus(view, config) {
            const statusElement = view.querySelector('#webServerStatusText');
            const port = finiteInteger(config.ExternalWebServerPort, 8097);
            const enabled = config.EnableExternalWebServer === true;
            const checkId = (this.webServerStatusCheckId || 0) + 1;
            this.webServerStatusCheckId = checkId;

            if (this.webServerStatusTimeout) {
                clearTimeout(this.webServerStatusTimeout);
                this.webServerStatusTimeout = null;
            }
            if (this.webServerStatusAbortController) {
                this.webServerStatusAbortController.abort();
                this.webServerStatusAbortController = null;
            }

            if (!enabled) {
                statusElement.textContent = '外部控制台未启用；不影响 Emby 内的一起看同步。';
                statusElement.style.color = '#999';
                this.setStatusElement(view, '#heroServerStatusText', '未启用', 'neutral');
                return;
            }

            const dashboardUrl = this.getDashboardUrl(config);
            if (!dashboardUrl) {
                statusElement.textContent = '反向代理模式：请通过代理公开地址检查控制台。';
                statusElement.style.color = '#2196F3';
                this.setStatusElement(view, '#heroServerStatusText', '由反向代理提供', 'info');
                return;
            }

            statusElement.textContent = '正在检查……';
            statusElement.style.color = '#FFA500';
            this.setStatusElement(view, '#heroServerStatusText', '正在检查……', 'pending');

            const abortController = typeof AbortController === 'function'
                ? new AbortController()
                : null;
            const timeoutMs = Number.isFinite(this.statusCheckTimeoutMs)
                ? this.statusCheckTimeoutMs
                : 5000;
            this.webServerStatusAbortController = abortController;

            const timeoutPromise = new Promise((_, reject) => {
                this.webServerStatusTimeout = setTimeout(() => {
                    if (abortController) abortController.abort();
                    const timeoutError = new Error('status check timed out');
                    timeoutError.name = 'TimeoutError';
                    reject(timeoutError);
                }, timeoutMs);
            });
            const requestOptions = { cache: 'no-store' };
            if (abortController) requestOptions.signal = abortController.signal;

            Promise.race([fetch(dashboardUrl, requestOptions), timeoutPromise])
                .then(response => {
                    if (checkId !== this.webServerStatusCheckId) return;
                    if (response.ok) {
                        statusElement.innerHTML = `✓ 正在端口 ${port} 上运行<br><a href="${dashboardUrl}" target="_blank" style="color: #4CAF50;">打开控制台</a>`;
                        statusElement.style.color = '#4CAF50';
                        this.setStatusElement(view, '#heroServerStatusText', `端口 ${port} 正在运行`, 'success');
                    } else {
                        statusElement.textContent = `错误：HTTP ${response.status}`;
                        statusElement.style.color = '#F44336';
                        this.setStatusElement(view, '#heroServerStatusText', `HTTP ${response.status}`, 'error');
                    }
                })
                .catch(error => {
                    if (checkId !== this.webServerStatusCheckId) return;
                    const timedOut = error && (error.name === 'TimeoutError'
                        || (abortController && abortController.signal.aborted));
                    statusElement.innerHTML = timedOut
                        ? '连接检查超时；外部控制台当前未启用。请确认控制台端口和网络配置。'
                        : `✗ 未运行<br><small style="color: #999;">Windows 可能需要执行：<code style="background: #222; padding: 0.2em 0.4em; border-radius: 3px;">netsh http add urlacl url=http://*:${port}/ user="Everyone"</code></small>`;
                    statusElement.style.color = '#F44336';
                    this.setStatusElement(
                        view,
                        '#heroServerStatusText',
                        timedOut ? '未启用' : '未运行',
                        timedOut ? 'neutral' : 'error');
                })
                .finally(() => {
                    if (checkId !== this.webServerStatusCheckId) return;
                    if (this.webServerStatusTimeout) {
                        clearTimeout(this.webServerStatusTimeout);
                        this.webServerStatusTimeout = null;
                    }
                    this.webServerStatusAbortController = null;
                });
        }

        async hashPassword(password) {
            const passwordBytes = new TextEncoder().encode(password);
            const salt = globalThis.crypto.getRandomValues(new Uint8Array(16));
            const key = await globalThis.crypto.subtle.importKey(
                'raw',
                passwordBytes,
                { name: 'PBKDF2' },
                false,
                ['deriveBits']
            );
            const derivedBits = await globalThis.crypto.subtle.deriveBits(
                {
                    name: 'PBKDF2',
                    salt: salt,
                    iterations: 600000,
                    hash: 'SHA-1'
                },
                key,
                256
            );
            const hashBytes = new Uint8Array(derivedBits);
            const encoded = new Uint8Array(salt.length + hashBytes.length);
            encoded.set(salt, 0);
            encoded.set(hashBytes, salt.length);
            return btoa(String.fromCharCode.apply(null, Array.from(encoded)));
        }

        generateGuid() {
            return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function(c) {
                const r = Math.random() * 16 | 0;
                const v = c === 'x' ? r : (r & 0x3 | 0x8);
                return v.toString(16);
            });
        }

        onResume(options) {
            this.isViewPaused = false;
            super.onResume(options);
            this.loadData(this.view);
        }

        onPause(options) {
            this.isViewPaused = true;
            this.dataLoadRequestId = (this.dataLoadRequestId || 0) + 1;
            this.recentContentRequestVersion = (this.recentContentRequestVersion || 0) + 1;
            this.partyRuntimeRequestVersion = (this.partyRuntimeRequestVersion || 0) + 1;
            this.stopPartyRuntimeRefresh();
            if (this.webServerStatusAbortController) {
                this.webServerStatusAbortController.abort();
            }
            if (this.webServerStatusTimeout) {
                clearTimeout(this.webServerStatusTimeout);
                this.webServerStatusTimeout = null;
            }
            if (super.onPause) super.onPause(options);
        }
    }
});
