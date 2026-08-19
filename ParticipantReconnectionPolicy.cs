using System;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Decides when a participant's next progress report represents a return from a
    /// transport/client disconnect and therefore needs an immediate authoritative sync.
    /// </summary>
    public static class ParticipantReconnectionPolicy
    {
        public static bool RequiresResync(
            DateTime lastActivityAtUtc,
            DateTime nowUtc,
            TimeSpan reconnectGap)
        {
            if (lastActivityAtUtc == DateTime.MinValue
                || reconnectGap <= TimeSpan.Zero
                || nowUtc <= lastActivityAtUtc)
            {
                return false;
            }

            return nowUtc - lastActivityAtUtc >= reconnectGap;
        }
    }
}
