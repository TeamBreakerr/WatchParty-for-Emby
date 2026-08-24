using MediaBrowser.Model.Session;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Identifies the one Emby playback-progress event that explicitly describes a
    /// stream/player replacement. Ordinary progress must never be allowed to replace a
    /// registered PlaySessionId, because delayed callbacks from an older player can carry
    /// a different position and seize the authoritative clock.
    /// </summary>
    public static class PlaybackGenerationAdoptionPolicy
    {
        public static bool IsQualityChange(ProgressEvent eventName)
        {
            return eventName == ProgressEvent.QualityChange;
        }

        /// <summary>
        /// A generation replacement is a transport/player lifecycle event, not a user
        /// pause or resume. Keep that event from entering either the immediate dispatch
        /// path or the delayed master pause debouncer.
        /// </summary>
        public static bool ShouldApplyPauseTransition(
            bool adoptedPlaybackGeneration,
            bool isMaster,
            bool deferMasterPauseTransition,
            bool ignoreMasterPauseTransition)
        {
            return !adoptedPlaybackGeneration
                && (!isMaster
                    || (!deferMasterPauseTransition && !ignoreMasterPauseTransition));
        }

        public static bool ResolveEffectivePauseState(
            bool adoptedPlaybackGeneration,
            bool previousPauseState,
            bool reportedPauseState,
            bool applyPauseTransition)
        {
            return adoptedPlaybackGeneration
                ? previousPauseState
                : (applyPauseTransition ? reportedPauseState : previousPauseState);
        }
    }
}
