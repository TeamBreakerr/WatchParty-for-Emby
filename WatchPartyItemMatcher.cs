using System;
using System.Collections.Generic;
using System.Globalization;

namespace WatchPartyForEmby
{
    public static class WatchPartyItemMatcher
    {
        public static WatchPartyItem FindActiveParty(
            IEnumerable<WatchPartyItem> parties,
            Guid itemId,
            long internalItemId)
        {
            return FindActiveParty(
                parties,
                new WatchPartyMediaIdentity
                {
                    ItemId = itemId,
                    InternalItemId = internalItemId
                },
                configuredItemResolver: null);
        }

        public static WatchPartyItem FindActiveParty(
            IEnumerable<WatchPartyItem> parties,
            WatchPartyMediaIdentity playingItem,
            Func<string, WatchPartyMediaIdentity> configuredItemResolver)
        {
            if (parties == null || playingItem == null)
            {
                return null;
            }

            WatchPartyItem exactMatch = null;
            WatchPartyItem equivalentMatch = null;
            var exactMatchCount = 0;
            var equivalentMatchCount = 0;

            foreach (var party in parties)
            {
                if (party?.IsActive != true)
                {
                    continue;
                }

                if (MatchesItemId(
                        party.ItemId,
                        playingItem.ItemId,
                        playingItem.InternalItemId)
                    || !string.IsNullOrEmpty(FindEpisodeItemId(
                        party,
                        playingItem.ItemId,
                        playingItem.InternalItemId)))
                {
                    exactMatch = party;
                    exactMatchCount++;
                    continue;
                }

                if (party.IsSeriesParty
                    || configuredItemResolver == null
                    || !AreEquivalent(
                        configuredItemResolver(party.ItemId),
                        playingItem))
                {
                    continue;
                }

                equivalentMatch = party;
                equivalentMatchCount++;
            }

            if (exactMatchCount > 1)
            {
                return null;
            }

            if (exactMatchCount == 1)
            {
                return exactMatch;
            }

            return equivalentMatchCount == 1 ? equivalentMatch : null;
        }

        public static bool HasActiveBindingConflict(IEnumerable<WatchPartyItem> parties)
        {
            if (parties == null)
            {
                return false;
            }

            var boundItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var party in parties)
            {
                if (party?.IsActive != true)
                {
                    continue;
                }

                var partyItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(party.ItemId))
                {
                    partyItemIds.Add(CanonicalizeItemId(party.ItemId));
                }

                if (party.IsSeriesParty && party.EpisodeQueue != null)
                {
                    foreach (var episode in party.EpisodeQueue)
                    {
                        if (!string.IsNullOrWhiteSpace(episode?.ItemId))
                        {
                            partyItemIds.Add(CanonicalizeItemId(episode.ItemId));
                        }
                    }
                }

                foreach (var boundItemId in partyItemIds)
                {
                    if (!boundItemIds.Add(boundItemId))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static string FindEpisodeItemId(
            WatchPartyItem party,
            Guid itemId,
            long internalItemId)
        {
            if (party?.IsSeriesParty != true || party.EpisodeQueue == null)
            {
                return null;
            }

            foreach (var episode in party.EpisodeQueue)
            {
                if (episode != null && MatchesItemId(episode.ItemId, itemId, internalItemId))
                {
                    return episode.ItemId;
                }
            }

            return null;
        }

        private static bool MatchesItemId(string configuredItemId, Guid itemId, long internalItemId)
        {
            if (string.IsNullOrWhiteSpace(configuredItemId))
            {
                return false;
            }

            if (Guid.TryParse(configuredItemId, out var configuredGuid))
            {
                return configuredGuid == itemId;
            }

            return long.TryParse(
                    configuredItemId,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var configuredInternalId)
                && configuredInternalId == internalItemId;
        }

        private static bool AreEquivalent(
            WatchPartyMediaIdentity configuredItem,
            WatchPartyMediaIdentity playingItem)
        {
            if (configuredItem == null || playingItem == null)
            {
                return false;
            }

            if (KnownItemIds(configuredItem).Overlaps(KnownItemIds(playingItem)))
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(configuredItem.PresentationUniqueKey)
                && !string.IsNullOrWhiteSpace(playingItem.PresentationUniqueKey)
                && string.Equals(
                    configuredItem.PresentationUniqueKey,
                    playingItem.PresentationUniqueKey,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static HashSet<string> KnownItemIds(WatchPartyMediaIdentity item)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (item.ItemId != Guid.Empty)
            {
                ids.Add(CanonicalizeItemId(item.ItemId.ToString()));
            }

            if (item.InternalItemId > 0)
            {
                ids.Add(CanonicalizeItemId(
                    item.InternalItemId.ToString(CultureInfo.InvariantCulture)));
            }

            if (item.MediaSourceItemIds == null)
            {
                return ids;
            }

            foreach (var mediaSourceItemId in item.MediaSourceItemIds)
            {
                if (string.IsNullOrWhiteSpace(mediaSourceItemId))
                {
                    continue;
                }

                var normalizedItemId = mediaSourceItemId.Trim();
                const string mediaSourcePrefix = "mediasource_";
                if (normalizedItemId.StartsWith(
                    mediaSourcePrefix,
                    StringComparison.OrdinalIgnoreCase))
                {
                    normalizedItemId = normalizedItemId.Substring(mediaSourcePrefix.Length);
                }

                ids.Add(CanonicalizeItemId(normalizedItemId));
            }

            return ids;
        }

        private static string CanonicalizeItemId(string configuredItemId)
        {
            var trimmedItemId = configuredItemId.Trim();
            if (Guid.TryParse(trimmedItemId, out var configuredGuid))
            {
                return "guid:" + configuredGuid.ToString("N");
            }

            if (long.TryParse(
                trimmedItemId,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var configuredInternalId))
            {
                return "internal:" + configuredInternalId.ToString(CultureInfo.InvariantCulture);
            }

            return "invalid:" + trimmedItemId;
        }
    }
}
