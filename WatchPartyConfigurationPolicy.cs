using System;
using System.Collections.Generic;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Applies the persisted room-configuration invariants in one place.
    /// </summary>
    public static class WatchPartyConfigurationPolicy
    {
        public static bool Normalize(WatchPartyItem party)
        {
            if (party == null)
            {
                return false;
            }

            var changed = false;

            if (party.AllowedUserIds == null)
            {
                party.AllowedUserIds = new List<string>();
                changed = true;
            }

            if (party.EpisodeQueue == null)
            {
                party.EpisodeQueue = new List<WatchPartyEpisode>();
                changed = true;
            }

            changed |= SetIfDifferent(
                party.PauseControl,
                NormalizePauseControl(party.PauseControl),
                value => party.PauseControl = value);

            changed |= SetIfDifferent(
                party.MaxParticipants,
                NormalizeBounded(party.MaxParticipants, 2, 100, 50),
                value => party.MaxParticipants = value);
            changed |= SetIfDifferent(
                party.MinReadyCount,
                Clamp(party.MinReadyCount, 1, party.MaxParticipants),
                value => party.MinReadyCount = value);
            changed |= SetIfDifferent(
                party.SyncToleranceSeconds,
                NormalizeBounded(party.SyncToleranceSeconds, 1, 60, 10),
                value => party.SyncToleranceSeconds = value);
            changed |= SetIfDifferent(
                party.MaxBufferThresholdSeconds,
                NormalizeBounded(party.MaxBufferThresholdSeconds, 10, 120, 30),
                value => party.MaxBufferThresholdSeconds = value);
            changed |= SetIfDifferent(
                party.InactiveTimeoutMinutes,
                NormalizeBounded(party.InactiveTimeoutMinutes, 5, 120, 15),
                value => party.InactiveTimeoutMinutes = value);

            if (IsMissing(party.MasterUserId) && !IsMissing(party.HostUserId))
            {
                party.MasterUserId = party.HostUserId;
                changed = true;
            }

            if (!IsMissing(party.MasterUserId)
                && !string.Equals(party.HostUserId, party.MasterUserId, StringComparison.Ordinal))
            {
                party.HostUserId = party.MasterUserId;
                changed = true;
            }

            if (party.AllowedUserIds.Count > 0
                && !IsMissing(party.MasterUserId)
                && !ContainsUserId(party.AllowedUserIds, party.MasterUserId))
            {
                party.AllowedUserIds.Add(party.MasterUserId);
                changed = true;
            }

            if (!party.IsWaitingRoom && party.AutoStartWhenReady)
            {
                party.AutoStartWhenReady = false;
                changed = true;
            }

            return SeriesPartyQueue.Repair(party) || changed;
        }

        private static string NormalizePauseControl(string pauseControl)
        {
            switch (PauseControlModeParser.Parse(pauseControl))
            {
                case PauseControlMode.Host:
                    return "Host";
                case PauseControlMode.Vote:
                    return "Vote";
                default:
                    return "Anyone";
            }
        }

        private static int NormalizeBounded(int value, int minimum, int maximum, int defaultValue)
        {
            return value == 0 ? defaultValue : Clamp(value, minimum, maximum);
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Min(maximum, Math.Max(minimum, value));
        }

        private static bool ContainsUserId(IEnumerable<string> userIds, string expectedUserId)
        {
            foreach (var userId in userIds)
            {
                if (string.Equals(userId, expectedUserId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsMissing(string value)
        {
            return string.IsNullOrWhiteSpace(value);
        }

        private static bool SetIfDifferent<T>(T current, T normalized, Action<T> setter)
        {
            if (EqualityComparer<T>.Default.Equals(current, normalized))
            {
                return false;
            }

            setter(normalized);
            return true;
        }
    }
}
