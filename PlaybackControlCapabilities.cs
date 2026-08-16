using System;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Decides whether a session can receive pause-state commands. Official Emby
    /// clients transiently clear SupportsRemoteControl while their player is being
    /// recreated, even though their server command channel remains usable. Third-party
    /// clients must still explicitly advertise support to avoid false-positive control.
    /// </summary>
    public static class PlaybackControlCapabilities
    {
        public static bool CanReceivePauseState(string client, bool supportsRemoteControl)
        {
            if (supportsRemoteControl)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(client))
            {
                return false;
            }

            return string.Equals(client, "Emby", StringComparison.OrdinalIgnoreCase)
                || client.StartsWith("Emby ", StringComparison.OrdinalIgnoreCase);
        }
    }
}
