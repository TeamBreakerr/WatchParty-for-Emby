using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;

namespace WatchPartyForEmby
{
    internal sealed class WatchPartyMediaIdentityResolver
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<string, WatchPartyMediaIdentity> _configuredItemCache =
            new ConcurrentDictionary<string, WatchPartyMediaIdentity>(StringComparer.OrdinalIgnoreCase);

        public WatchPartyMediaIdentityResolver(
            ILibraryManager libraryManager,
            ILogger logger)
        {
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void ClearConfiguredItemCache()
        {
            _configuredItemCache.Clear();
        }

        public WatchPartyMediaIdentity FromPlaybackItem(BaseItem item)
        {
            return CreateIdentity(item, includeMediaSources: false);
        }

        public WatchPartyMediaIdentity ResolveConfiguredItem(string configuredItemId)
        {
            if (string.IsNullOrWhiteSpace(configuredItemId))
            {
                return null;
            }

            if (_configuredItemCache.TryGetValue(configuredItemId, out var cachedIdentity))
            {
                return cachedIdentity;
            }

            var identity = CreateIdentity(
                _libraryManager.GetItemById(configuredItemId),
                includeMediaSources: true);
            if (identity != null)
            {
                _configuredItemCache.TryAdd(configuredItemId, identity);
            }

            return identity;
        }

        private WatchPartyMediaIdentity CreateIdentity(
            BaseItem item,
            bool includeMediaSources)
        {
            if (item == null)
            {
                return null;
            }

            var mediaSourceItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (includeMediaSources)
            {
                TryAddMediaSourceItemIds(item, mediaSourceItemIds);
            }

            return new WatchPartyMediaIdentity
            {
                ItemId = item.Id,
                InternalItemId = item.InternalId,
                PresentationUniqueKey = item.GetPresentationUniqueKey(),
                MediaSourceItemIds = mediaSourceItemIds
            };
        }

        private void TryAddMediaSourceItemIds(
            BaseItem item,
            HashSet<string> mediaSourceItemIds)
        {
            try
            {
                var libraryOptions = _libraryManager.GetLibraryOptions(item);
                foreach (var mediaSource in item.GetMediaSources(
                    false,
                    false,
                    libraryOptions))
                {
                    if (!string.IsNullOrWhiteSpace(mediaSource?.ItemId))
                    {
                        mediaSourceItemIds.Add(mediaSource.ItemId);
                    }

                    if (!string.IsNullOrWhiteSpace(mediaSource?.Id))
                    {
                        mediaSourceItemIds.Add(mediaSource.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(
                    $"[Watch Party] Could not inspect media versions for item " +
                    $"{item.InternalId}: {ex.Message}");
            }
        }
    }
}
