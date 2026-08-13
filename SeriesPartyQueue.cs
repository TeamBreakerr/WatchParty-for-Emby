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
        private static readonly long CompletionWindowTicks = TimeSpan.FromSeconds(30).Ticks;

        public static void Initialize(
            WatchPartyItem party,
            IEnumerable<WatchPartyEpisode> episodes,
            string startingItemId,
            string seriesName)
        {
            if (party == null)
            {
                throw new ArgumentNullException(nameof(party));
            }

            var orderedEpisodes = (episodes ?? Enumerable.Empty<WatchPartyEpisode>())
                .Where(episode => episode != null && !string.IsNullOrEmpty(episode.ItemId))
                .GroupBy(episode => episode.ItemId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(episode => episode.SeasonNumber)
                .ThenBy(episode => episode.EpisodeNumber)
                .ThenBy(episode => episode.ItemName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (orderedEpisodes.Count == 0)
            {
                throw new ArgumentException("A series party requires at least one episode.", nameof(episodes));
            }

            var startingIndex = orderedEpisodes.FindIndex(
                episode => string.Equals(episode.ItemId, startingItemId, StringComparison.OrdinalIgnoreCase));
            if (startingIndex < 0)
            {
                startingIndex = 0;
            }

            party.IsSeriesParty = true;
            party.SeriesName = seriesName;
            party.EpisodeQueue = orderedEpisodes;
            party.CurrentEpisodeIndex = startingIndex;
            ApplyCurrentEpisode(party);
        }

        public static bool Repair(WatchPartyItem party)
        {
            if (party == null)
            {
                return false;
            }

            party.EpisodeQueue = party.EpisodeQueue ?? new List<WatchPartyEpisode>();
            if (!party.IsSeriesParty || party.EpisodeQueue.Count == 0)
            {
                return false;
            }

            var originalIndex = party.CurrentEpisodeIndex;
            var resolvedIndex = party.EpisodeQueue.FindIndex(episode =>
                episode != null
                && (string.Equals(episode.ItemId, party.CurrentEpisodeId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(episode.ItemId, party.ItemId, StringComparison.OrdinalIgnoreCase)));

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

            return originalIndex != party.CurrentEpisodeIndex
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

        public static bool ContainsEpisode(WatchPartyItem party, string itemId)
        {
            return party?.IsSeriesParty == true
                && party.EpisodeQueue != null
                && party.EpisodeQueue.Any(episode => episode != null
                    && string.Equals(episode.ItemId, itemId, StringComparison.OrdinalIgnoreCase));
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

            if (!IsNaturalCompletion(positionTicks, runtimeTicks))
            {
                return SeriesPartyAdvanceResult.NotCompleted;
            }

            party.IsPlaying = false;
            if (party.CurrentEpisodeIndex >= party.EpisodeQueue.Count - 1)
            {
                return SeriesPartyAdvanceResult.EndOfQueue;
            }

            party.CurrentEpisodeIndex++;
            party.CurrentPositionTicks = 0;
            ApplyCurrentEpisode(party);
            return SeriesPartyAdvanceResult.Advanced;
        }

        public static bool IsNaturalCompletion(long positionTicks, long runtimeTicks)
        {
            if (runtimeTicks <= 0 || positionTicks < 0)
            {
                return false;
            }

            var completionThreshold = Math.Max(
                runtimeTicks - CompletionWindowTicks,
                (long)Math.Floor(runtimeTicks * 0.95));
            return positionTicks >= Math.Max(0, completionThreshold);
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
