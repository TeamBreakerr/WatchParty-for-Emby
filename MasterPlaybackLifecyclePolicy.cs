using System;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Describes the lifecycle decision for one accepted PlaybackStopped event.
    /// The event handler still performs the I/O (registry updates and commands),
    /// while this policy owns the rules that decide which state transition is valid.
    /// </summary>
    public enum PlaybackStopDisposition
    {
        RetainParticipantForEpisodeTransition,
        MarkParticipantDormant,
        RetainMasterForEpisodeTransition,
        RetireMaster
    }

    public sealed class PlaybackStopContext
    {
        public bool IsMaster { get; set; }
        public bool IsSeriesParty { get; set; }
        public bool IsExpectedEpisodeTransition { get; set; }
    }

    public sealed class PlaybackStopDecision
    {
        internal PlaybackStopDecision(
            PlaybackStopDisposition disposition,
            bool retainPlayer,
            bool removeMaster,
            bool captureSeriesHandoff)
        {
            Disposition = disposition;
            RetainPlayer = retainPlayer;
            RemoveMaster = removeMaster;
            CaptureSeriesHandoff = captureSeriesHandoff;
        }

        public PlaybackStopDisposition Disposition { get; }

        /// <summary>
        /// A participant's existing player must remain open while the master is
        /// between episodes, and must remain locally controllable after master stop.
        /// </summary>
        public bool RetainPlayer { get; }

        public bool RemoveMaster { get; }
        public bool CaptureSeriesHandoff { get; }

        /// <summary>
        /// Master Stop never becomes a broadcast Stop. A caller can use this invariant
        /// when deciding which commands to enqueue.
        /// </summary>
        public bool SendParticipantStop => false;
    }

    public enum MasterPlaybackStartDisposition
    {
        NotMaster,
        ContinueCurrentGeneration,
        ResumeCurrentEpisode,
        SwitchEpisode
    }

    public sealed class MasterPlaybackStartContext
    {
        public bool IsMaster { get; set; }
        public bool MasterWasInactiveBeforeStart { get; set; }
        public bool IsSeriesParty { get; set; }
        public string StartedEpisodeId { get; set; }
        public string CurrentEpisodeId { get; set; }
        public bool HasQueuedEpisodeSelection { get; set; }
    }

    public sealed class MasterPlaybackStartDecision
    {
        internal MasterPlaybackStartDecision(
            MasterPlaybackStartDisposition disposition,
            bool retainParticipantPlayers,
            bool sendPlayNowToParticipants,
            bool clearHandoffAuthorizations)
        {
            Disposition = disposition;
            RetainParticipantPlayers = retainParticipantPlayers;
            SendPlayNowToParticipants = sendPlayNowToParticipants;
            ClearHandoffAuthorizations = clearHandoffAuthorizations;
        }

        public MasterPlaybackStartDisposition Disposition { get; }
        public bool RetainParticipantPlayers { get; }
        public bool SendPlayNowToParticipants { get; }
        public bool ClearHandoffAuthorizations { get; }
    }

    public sealed class MasterDepartureContext
    {
        public bool HasReplacementMaster { get; set; }
        public bool WasWaitingRoom { get; set; }
    }

    public sealed class MasterDepartureDecision
    {
        internal MasterDepartureDecision(
            bool promoteReplacementMaster,
            bool freezeAuthority,
            bool retainParticipantPlayers,
            bool preserveWaitingRoom)
        {
            PromoteReplacementMaster = promoteReplacementMaster;
            FreezeAuthority = freezeAuthority;
            RetainParticipantPlayers = retainParticipantPlayers;
            PreserveWaitingRoom = preserveWaitingRoom;
        }

        public bool PromoteReplacementMaster { get; }
        public bool FreezeAuthority { get; }
        public bool RetainParticipantPlayers { get; }
        public bool PreserveWaitingRoom { get; }

        /// <summary>
        /// A departure invalidates all delayed commands from the old generation.
        /// </summary>
        public bool CancelPendingWork => true;
    }

    /// <summary>
    /// Pure lifecycle rules for the authoritative master playback generation.
    /// Keeping these decisions independent from Emby objects makes the production
    /// event sequence deterministic and directly testable.
    /// </summary>
    public static class MasterPlaybackLifecyclePolicy
    {
        public static PlaybackStopDecision DecidePlaybackStopped(
            PlaybackStopContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (!context.IsMaster)
            {
                return context.IsSeriesParty && context.IsExpectedEpisodeTransition
                    ? new PlaybackStopDecision(
                        PlaybackStopDisposition.RetainParticipantForEpisodeTransition,
                        retainPlayer: true,
                        removeMaster: false,
                        captureSeriesHandoff: false)
                    : new PlaybackStopDecision(
                        PlaybackStopDisposition.MarkParticipantDormant,
                        retainPlayer: false,
                        removeMaster: false,
                        captureSeriesHandoff: false);
            }

            return context.IsSeriesParty && context.IsExpectedEpisodeTransition
                ? new PlaybackStopDecision(
                    PlaybackStopDisposition.RetainMasterForEpisodeTransition,
                    retainPlayer: true,
                    removeMaster: false,
                    captureSeriesHandoff: false)
                : new PlaybackStopDecision(
                    PlaybackStopDisposition.RetireMaster,
                    retainPlayer: true,
                    removeMaster: true,
                    captureSeriesHandoff: context.IsSeriesParty);
        }

        public static MasterPlaybackStartDecision DecideMasterPlaybackStarted(
            MasterPlaybackStartContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (!context.IsMaster)
            {
                return new MasterPlaybackStartDecision(
                    MasterPlaybackStartDisposition.NotMaster,
                    retainParticipantPlayers: true,
                    sendPlayNowToParticipants: false,
                    clearHandoffAuthorizations: false);
            }

            var sameEpisode = !string.IsNullOrWhiteSpace(context.StartedEpisodeId)
                && !string.IsNullOrWhiteSpace(context.CurrentEpisodeId)
                && string.Equals(
                    context.StartedEpisodeId,
                    context.CurrentEpisodeId,
                    StringComparison.OrdinalIgnoreCase);
            if (context.IsSeriesParty
                && context.MasterWasInactiveBeforeStart
                && sameEpisode)
            {
                return new MasterPlaybackStartDecision(
                    MasterPlaybackStartDisposition.ResumeCurrentEpisode,
                    retainParticipantPlayers: true,
                    sendPlayNowToParticipants: false,
                    clearHandoffAuthorizations: true);
            }

            var switchedEpisode = context.IsSeriesParty
                && context.HasQueuedEpisodeSelection
                && !sameEpisode
                && !string.IsNullOrWhiteSpace(context.StartedEpisodeId);
            if (switchedEpisode)
            {
                return new MasterPlaybackStartDecision(
                    MasterPlaybackStartDisposition.SwitchEpisode,
                    retainParticipantPlayers: false,
                    sendPlayNowToParticipants: true,
                    clearHandoffAuthorizations: false);
            }

            return new MasterPlaybackStartDecision(
                MasterPlaybackStartDisposition.ContinueCurrentGeneration,
                retainParticipantPlayers: true,
                sendPlayNowToParticipants: false,
                clearHandoffAuthorizations: false);
        }

        public static MasterDepartureDecision DecideMasterDeparture(
            MasterDepartureContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            return new MasterDepartureDecision(
                promoteReplacementMaster: context.HasReplacementMaster,
                freezeAuthority: !context.HasReplacementMaster,
                retainParticipantPlayers: true,
                preserveWaitingRoom: context.WasWaitingRoom);
        }
    }
}
