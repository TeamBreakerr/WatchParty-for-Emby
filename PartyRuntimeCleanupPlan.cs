using System;
using System.Collections.Generic;
using System.Linq;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Identifies rooms whose in-memory playback state must be discarded after a
    /// configuration update. Deactivation is a runtime teardown just like deletion.
    /// </summary>
    public sealed class PartyRuntimeCleanupPlan
    {
        private PartyRuntimeCleanupPlan(
            HashSet<string> currentPartyIds,
            HashSet<string> currentActivePartyIds,
            IReadOnlyList<string> removedPartyIds,
            IReadOnlyList<string> deactivatedPartyIds)
        {
            CurrentPartyIds = currentPartyIds;
            CurrentActivePartyIds = currentActivePartyIds;
            RemovedPartyIds = removedPartyIds;
            DeactivatedPartyIds = deactivatedPartyIds;
            PartyIdsToTeardown = removedPartyIds
                .Concat(deactivatedPartyIds)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        public IReadOnlyCollection<string> CurrentPartyIds { get; }
        public IReadOnlyCollection<string> CurrentActivePartyIds { get; }
        public IReadOnlyList<string> RemovedPartyIds { get; }
        public IReadOnlyList<string> DeactivatedPartyIds { get; }
        public IReadOnlyList<string> PartyIdsToTeardown { get; }

        public static PartyRuntimeCleanupPlan Create(
            IEnumerable<string> previouslyTrackedPartyIds,
            IEnumerable<string> previouslyActivePartyIds,
            IEnumerable<WatchPartyItem> currentParties)
        {
            var current = (currentParties ?? Array.Empty<WatchPartyItem>())
                .Where(party => party != null && !string.IsNullOrEmpty(party.Id))
                .ToList();
            var currentPartyIds = new HashSet<string>(
                current.Select(party => party.Id),
                StringComparer.Ordinal);
            var currentActivePartyIds = new HashSet<string>(
                current.Where(party => party.IsActive).Select(party => party.Id),
                StringComparer.Ordinal);
            var previousPartyIds = new HashSet<string>(
                previouslyTrackedPartyIds ?? Array.Empty<string>(),
                StringComparer.Ordinal);
            var previousActivePartyIds = new HashSet<string>(
                previouslyActivePartyIds ?? Array.Empty<string>(),
                StringComparer.Ordinal);

            return new PartyRuntimeCleanupPlan(
                currentPartyIds,
                currentActivePartyIds,
                previousPartyIds.Except(currentPartyIds).ToList(),
                previousActivePartyIds
                    .Except(currentActivePartyIds)
                    .Where(currentPartyIds.Contains)
                    .ToList());
        }
    }
}
