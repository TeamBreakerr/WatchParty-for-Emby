using System;

namespace WatchPartyForEmby
{
    public enum PlaybackStopDisposition
    {
        RetainForEpisodeTransition,
        ResolveStoppedGeneration
    }

    public sealed class PlaybackStopContext
    {
        public bool IsSeriesParty { get; set; }
        public bool IsExpectedEpisodeTransition { get; set; }
    }

    public enum MasterPlaybackStartDisposition
    {
        NotMaster,
        ContinueCurrentGeneration,
        ReestablishAuthority,
        SwitchEpisode
    }

    public sealed class MasterPlaybackStartContext
    {
        public bool IsMaster { get; set; }
        public bool AuthorityWasReestablished { get; set; }
        public bool IsSeriesParty { get; set; }
        public string StartedEpisodeId { get; set; }
        public string CurrentEpisodeId { get; set; }
        public bool HasQueuedEpisodeSelection { get; set; }
    }

    /// <summary>
    /// Pure lifecycle rules for the authoritative master playback generation.
    /// The result is one exhaustive action instead of a correlated set of flags.
    /// </summary>
    public static class MasterPlaybackLifecyclePolicy
    {
        public static PlaybackStopDisposition DecidePlaybackStopped(
            PlaybackStopContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            return context.IsSeriesParty && context.IsExpectedEpisodeTransition
                ? PlaybackStopDisposition.RetainForEpisodeTransition
                : PlaybackStopDisposition.ResolveStoppedGeneration;
        }

        public static MasterPlaybackStartDisposition DecideMasterPlaybackStarted(
            MasterPlaybackStartContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (!context.IsMaster)
            {
                return MasterPlaybackStartDisposition.NotMaster;
            }

            var sameEpisode = !string.IsNullOrWhiteSpace(context.StartedEpisodeId)
                && !string.IsNullOrWhiteSpace(context.CurrentEpisodeId)
                && string.Equals(
                    context.StartedEpisodeId,
                    context.CurrentEpisodeId,
                    StringComparison.OrdinalIgnoreCase);
            if (context.AuthorityWasReestablished
                && (!context.IsSeriesParty || sameEpisode))
            {
                return MasterPlaybackStartDisposition.ReestablishAuthority;
            }

            return context.IsSeriesParty
                && context.HasQueuedEpisodeSelection
                && !sameEpisode
                && !string.IsNullOrWhiteSpace(context.StartedEpisodeId)
                    ? MasterPlaybackStartDisposition.SwitchEpisode
                    : MasterPlaybackStartDisposition.ContinueCurrentGeneration;
        }
    }

}
