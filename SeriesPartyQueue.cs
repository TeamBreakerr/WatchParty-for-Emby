using System;
using System.Collections.Generic;
using System.Linq;

namespace WatchPartyForEmby
{
    public enum SeriesPartyAdvanceResult
    {
        NotSeriesParty,
        ItemMismatch,
        NotCompleted,
        EndOfQueue,
        Advanced
    }

    public static class SeriesPartyQueue
    {
        public static bool Repair(WatchPartyItem party)
        {
            if (party == null)
            {
                return false;
            }

            var originalQueue = party.EpisodeQueue ?? new List<WatchPartyEpisode>();
            if (!party.IsSeriesParty || originalQueue.Count == 0)
            {
                party.EpisodeQueue = originalQueue;
                return false;
            }

            var normalizedQueue = NormalizeEpisodes(originalQueue);
            var queueChanged = originalQueue.Count != normalizedQueue.Count
                || !originalQueue.Select(episode => episode?.ItemId)
                    .SequenceEqual(normalizedQueue.Select(episode => episode.ItemId), StringComparer.OrdinalIgnoreCase);
            party.EpisodeQueue = normalizedQueue;

            var originalIndex = party.CurrentEpisodeIndex;
            var resolvedIndex = party.EpisodeQueue.FindIndex(episode =>
                string.Equals(episode.ItemId, party.CurrentEpisodeId, StringComparison.OrdinalIgnoreCase));
            if (resolvedIndex < 0)
            {
                resolvedIndex = party.EpisodeQueue.FindIndex(episode =>
                    string.Equals(episode.ItemId, party.ItemId, StringComparison.OrdinalIgnoreCase));
            }

            if (resolvedIndex < 0)
            {
                resolvedIndex = originalIndex >= 0 && originalIndex < party.EpisodeQueue.Count
                    ? originalIndex
                    : 0;
            }

            party.CurrentEpisodeIndex = resolvedIndex;
            var previousItemId = party.ItemId;
            var previousEpisodeId = party.CurrentEpisodeId;
            var previousItemName = party.ItemName;
            ApplyCurrentEpisode(party);

            return queueChanged
                || originalIndex != party.CurrentEpisodeIndex
                || !string.Equals(previousItemId, party.ItemId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(previousEpisodeId, party.CurrentEpisodeId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(previousItemName, party.ItemName, StringComparison.Ordinal);
        }

        public static WatchPartyEpisode GetCurrentEpisode(WatchPartyItem party)
        {
            if (party == null || !party.IsSeriesParty || party.EpisodeQueue == null)
            {
                return null;
            }

            return party.CurrentEpisodeIndex >= 0 && party.CurrentEpisodeIndex < party.EpisodeQueue.Count
                ? party.EpisodeQueue[party.CurrentEpisodeIndex]
                : null;
        }

        public static WatchPartyEpisode GetNextEpisode(WatchPartyItem party)
        {
            if (party == null
                || !party.IsSeriesParty
                || party.EpisodeQueue == null)
            {
                return null;
            }

            var nextIndex = party.CurrentEpisodeIndex + 1;
            return nextIndex >= 0 && nextIndex < party.EpisodeQueue.Count
                ? party.EpisodeQueue[nextIndex]
                : null;
        }

        public static bool TrySelectEpisode(WatchPartyItem party, string episodeItemId)
        {
            if (party?.IsSeriesParty != true
                || party.EpisodeQueue == null
                || string.IsNullOrEmpty(episodeItemId))
            {
                return false;
            }

            var selectedIndex = party.EpisodeQueue.FindIndex(episode =>
                episode != null
                && string.Equals(episode.ItemId, episodeItemId, StringComparison.OrdinalIgnoreCase));
            if (selectedIndex < 0)
            {
                return false;
            }

            party.CurrentEpisodeIndex = selectedIndex;
            party.CurrentPositionTicks = 0;
            party.IsPlaying = false;
            ApplyCurrentEpisode(party);
            return true;
        }

        public static bool RemoveUnavailableEpisodes(
            WatchPartyItem party,
            Func<string, bool> isAvailable)
        {
            if (party?.IsSeriesParty != true
                || party.EpisodeQueue == null
                || isAvailable == null)
            {
                return false;
            }

            var originalCount = party.EpisodeQueue.Count;
            var originalIndex = party.CurrentEpisodeIndex;
            var originalCurrentEpisodeId = party.CurrentEpisodeId ?? party.ItemId;
            party.EpisodeQueue = party.EpisodeQueue
                .Where(episode => episode != null
                    && !string.IsNullOrEmpty(episode.ItemId)
                    && isAvailable(episode.ItemId))
                .ToList();

            if (party.EpisodeQueue.Count == 0)
            {
                party.CurrentEpisodeIndex = -1;
                party.CurrentEpisodeId = null;
                party.CurrentPositionTicks = 0;
                party.IsPlaying = false;
                return originalCount > 0;
            }

            var currentEpisodeStillExists = party.EpisodeQueue.Any(episode =>
                string.Equals(
                    episode.ItemId,
                    originalCurrentEpisodeId,
                    StringComparison.OrdinalIgnoreCase));
            if (!currentEpisodeStillExists)
            {
                var replacementIndex = Math.Min(
                    Math.Max(0, originalIndex),
                    party.EpisodeQueue.Count - 1);
                var replacementEpisode = party.EpisodeQueue[replacementIndex];
                party.CurrentEpisodeIndex = replacementIndex;
                party.CurrentEpisodeId = replacementEpisode.ItemId;
                party.ItemId = replacementEpisode.ItemId;
                party.CurrentPositionTicks = 0;
                party.IsPlaying = false;
            }

            var queueChanged = originalCount != party.EpisodeQueue.Count;
            var stateChanged = Repair(party);
            return queueChanged || stateChanged;
        }

        public static SeriesPartyAdvanceResult TryAdvanceAfterStop(
            WatchPartyItem party,
            string completedItemId,
            long positionTicks,
            long runtimeTicks)
        {
            if (party?.IsSeriesParty != true || party.EpisodeQueue == null || party.EpisodeQueue.Count == 0)
            {
                return SeriesPartyAdvanceResult.NotSeriesParty;
            }

            var currentEpisode = GetCurrentEpisode(party);
            if (currentEpisode == null
                || !string.Equals(currentEpisode.ItemId, completedItemId, StringComparison.OrdinalIgnoreCase))
            {
                return SeriesPartyAdvanceResult.ItemMismatch;
            }

            // A Stop never selects another episode. Emby Web emits Stop both at the
            // end of an item and while a user manually changes streams/episodes, and a
            // stale PlaySession can carry an unrelated near-end position. The master's
            // next PlaybackStart is the only authoritative episode-selection event.
            return SeriesPartyAdvanceResult.NotCompleted;
        }

        private static List<WatchPartyEpisode> NormalizeEpisodes(IEnumerable<WatchPartyEpisode> episodes)
        {
            return (episodes ?? Enumerable.Empty<WatchPartyEpisode>())
                .Where(episode => episode != null && !string.IsNullOrEmpty(episode.ItemId))
                .GroupBy(episode => episode.ItemId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(episode => episode.SeasonNumber)
                .ThenBy(episode => episode.EpisodeNumber)
                .ThenBy(episode => episode.ItemName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void ApplyCurrentEpisode(WatchPartyItem party)
        {
            var currentEpisode = GetCurrentEpisode(party);
            if (currentEpisode == null)
            {
                return;
            }

            party.CurrentEpisodeId = currentEpisode.ItemId;
            party.ItemId = currentEpisode.ItemId;
            party.ItemName = currentEpisode.ItemName;
            party.ItemType = "Episode";
            party.SeasonId = currentEpisode.SeasonId;
        }
    }
}
