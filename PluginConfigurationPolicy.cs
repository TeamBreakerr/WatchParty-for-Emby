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
                Clamp(configuration.SyncOffsetMilliseconds, -10000, 10000),
                value => configuration.SyncOffsetMilliseconds = value);
            changed |= SetIfDifferent(
                configuration.ExternalWebServerPort,
                NormalizeRequired(configuration.ExternalWebServerPort, 1, 65535, 8097),
                value => configuration.ExternalWebServerPort = value);
            changed |= SetIfDifferent(
                configuration.ListenAddress,
                string.IsNullOrWhiteSpace(configuration.ListenAddress)
                    ? "127.0.0.1"
                    : configuration.ListenAddress.Trim(),
                value => configuration.ListenAddress = value);
            changed |= SetIfDifferent(
                configuration.SessionExpirationMinutes,
                NormalizeRequired(configuration.SessionExpirationMinutes, 5, 1440, 60),
                value => configuration.SessionExpirationMinutes = value);
            changed |= SetIfDifferent(
                configuration.RateLimitRequestsPerMinute,
                Clamp(configuration.RateLimitRequestsPerMinute, 0, 10000),
                value => configuration.RateLimitRequestsPerMinute = value);
            changed |= SetIfDifferent(
                configuration.RateLimitBlockDurationMinutes,
                NormalizeRequired(configuration.RateLimitBlockDurationMinutes, 1, 1440, 15),
                value => configuration.RateLimitBlockDurationMinutes = value);
            changed |= SetIfDifferent(
                configuration.HstsMaxAge,
                Clamp(configuration.HstsMaxAge, 0, 63072000),
                value => configuration.HstsMaxAge = value);
            changed |= SetIfDifferent(
                configuration.MaxFailedLoginAttempts,
                NormalizeRequired(configuration.MaxFailedLoginAttempts, 1, 100, 5),
                value => configuration.MaxFailedLoginAttempts = value);
            changed |= SetIfDifferent(
                configuration.LockoutDurationMinutes,
                NormalizeRequired(configuration.LockoutDurationMinutes, 1, 1440, 15),
                value => configuration.LockoutDurationMinutes = value);
            changed |= SetIfDifferent(
                configuration.LockoutWindowMinutes,
                NormalizeRequired(configuration.LockoutWindowMinutes, 1, 1440, 10),
                value => configuration.LockoutWindowMinutes = value);
            changed |= SetIfDifferent(
                configuration.MaxAuditLogEntries,
                Clamp(configuration.MaxAuditLogEntries, 0, 100000),
                value => configuration.MaxAuditLogEntries = value);

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
