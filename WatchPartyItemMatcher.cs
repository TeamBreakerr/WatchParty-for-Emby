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
            if (parties == null)
            {
                return null;
            }

            WatchPartyItem match = null;
            foreach (var party in parties)
            {
                if (party?.IsActive != true)
                {
                    continue;
                }

                if (MatchesItemId(party.ItemId, itemId, internalItemId)
                    || !string.IsNullOrEmpty(FindEpisodeItemId(party, itemId, internalItemId)))
                {
                    if (match != null)
                    {
                        return null;
                    }

                    match = party;
                }
            }

            return match;
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
