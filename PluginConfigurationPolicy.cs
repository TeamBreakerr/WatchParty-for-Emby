using System;
using System.Collections.Generic;

namespace WatchPartyForEmby
{
    public static class PluginConfigurationPolicy
    {
        public static bool Normalize(PluginConfiguration configuration)
        {
            if (configuration == null)
            {
                return false;
            }

            var changed = false;
            if (configuration.WatchParties == null)
            {
                configuration.WatchParties = new List<WatchPartyItem>();
                changed = true;
            }

            foreach (var party in configuration.WatchParties)
            {
                changed |= WatchPartyConfigurationPolicy.Normalize(party);
            }

            changed |= SetIfDifferent(
                configuration.SyncIntervalSeconds,
                NormalizeRequired(configuration.SyncIntervalSeconds, 1, 300, 5),
                value => configuration.SyncIntervalSeconds = value);
            changed |= SetIfDifferent(
                configuration.SyncOffsetMilliseconds,
                0,
                value => configuration.SyncOffsetMilliseconds = value);
            return changed;
        }

        private static int NormalizeRequired(
            int value,
            int minimum,
            int maximum,
            int defaultValue)
        {
            return value == 0 ? defaultValue : Clamp(value, minimum, maximum);
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Min(maximum, Math.Max(minimum, value));
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
