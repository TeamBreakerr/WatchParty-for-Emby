using System;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Decides whether one playback-state report should enter room pause control.
    /// A stale per-session cache must not hide a real participant transition, but
    /// synthetic reload/command echoes must never become room-wide controls.
    /// </summary>
    public static class PauseTransitionPolicy
    {
        public static bool ShouldHandle(
            bool isMaster,
            bool isWaitingRoom,
            bool isInitialParticipantReport,
            bool isSyntheticEcho,
            bool previousIsPaused,
            bool reportedIsPaused,
            bool authoritativeIsPlaying)
        {
            if (isWaitingRoom || isInitialParticipantReport || isSyntheticEcho)
            {
                return false;
            }

            if (reportedIsPaused != previousIsPaused)
            {
                return true;
            }

            // If the local cache is stale, a participant action can otherwise be
            // ignored forever. Let the caller enter the normal pause-policy decision
            // even in Host mode, where the coordinator will reject it and restore the
            // actor. Anyone/Vote modes can accept it and broadcast normally.
            return !isMaster && reportedIsPaused == authoritativeIsPlaying;
        }
    }
}
