namespace WatchPartyForEmby
{
    public enum PlaybackStateReporterRole
    {
        Master,
        Participant
    }

    public enum PlaybackStateAuthorityAction
    {
        Ignore,
        BroadcastMaster,
        RestoreParticipant
    }

    /// <summary>
    /// Enforces one-way playback-state authority. Only the master can change the
    /// room; a participant that diverges is restored to the master's state.
    /// </summary>
    public static class PauseTransitionPolicy
    {
        public static PlaybackStateAuthorityAction Decide(
            PlaybackStateReporterRole reporterRole,
            bool previousIsPaused,
            bool reportedIsPaused,
            bool authoritativeIsPlaying,
            bool hasActiveMaster)
        {
            if (reporterRole == PlaybackStateReporterRole.Master)
            {
                return reportedIsPaused != previousIsPaused
                    ? PlaybackStateAuthorityAction.BroadcastMaster
                    : PlaybackStateAuthorityAction.Ignore;
            }

            if (!hasActiveMaster)
            {
                return PlaybackStateAuthorityAction.Ignore;
            }

            var authoritativeIsPaused = !authoritativeIsPlaying;
            return reportedIsPaused != authoritativeIsPaused
                ? PlaybackStateAuthorityAction.RestoreParticipant
                : PlaybackStateAuthorityAction.Ignore;
        }
    }
}
