using System;
using System.Threading;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class MasterPlaybackLifecyclePolicyTests
    {
        [Fact]
        public void OrdinaryStopMustResolveTheCurrentPlaybackGeneration()
        {
            var disposition = MasterPlaybackLifecyclePolicy.DecidePlaybackStopped(
                new PlaybackStopContext
                {
                    IsSeriesParty = false,
                    IsExpectedEpisodeTransition = false
                });

            Assert.Equal(PlaybackStopDisposition.ResolveStoppedGeneration, disposition);
        }

        [Fact]
        public void SeriesStopRetainsOnlyAnExpectedEpisodeHandoff()
        {
            var handoff = MasterPlaybackLifecyclePolicy.DecidePlaybackStopped(
                new PlaybackStopContext
                {
                    IsSeriesParty = true,
                    IsExpectedEpisodeTransition = true
                });
            var ordinaryStop = MasterPlaybackLifecyclePolicy.DecidePlaybackStopped(
                new PlaybackStopContext
                {
                    IsSeriesParty = true,
                    IsExpectedEpisodeTransition = false
                });

            Assert.Equal(
                PlaybackStopDisposition.RetainForEpisodeTransition,
                handoff);
            Assert.Equal(
                PlaybackStopDisposition.ResolveStoppedGeneration,
                ordinaryStop);
        }

        [Fact]
        public void MasterStopThenSameEpisodeRestartCannotSelectFollowerPlayNow()
        {
            var stop = MasterPlaybackLifecyclePolicy.DecidePlaybackStopped(
                new PlaybackStopContext
                {
                    IsSeriesParty = true,
                    IsExpectedEpisodeTransition = false
                });
            var start = MasterPlaybackLifecyclePolicy.DecideMasterPlaybackStarted(
                new MasterPlaybackStartContext
                {
                    IsMaster = true,
                    MasterWasInactiveBeforeStart = true,
                    IsSeriesParty = true,
                    StartedEpisodeId = "episode-a",
                    CurrentEpisodeId = "episode-a"
                });

            Assert.Equal(PlaybackStopDisposition.ResolveStoppedGeneration, stop);
            Assert.Equal(MasterPlaybackStartDisposition.ResumeCurrentEpisode, start);
            Assert.NotEqual(MasterPlaybackStartDisposition.SwitchEpisode, start);
        }

        [Fact]
        public void SameEpisodeRestartDoesNotWakeADormantFollower()
        {
            using var dormancies = new ParticipantDormancyTracker(Timeout.InfiniteTimeSpan);
            dormancies.MarkDormant(
                "party",
                "ios",
                "old-playback",
                new DateTime(2026, 8, 27, 0, 0, 0, DateTimeKind.Utc));

            var start = MasterPlaybackLifecyclePolicy.DecideMasterPlaybackStarted(
                new MasterPlaybackStartContext
                {
                    IsMaster = true,
                    MasterWasInactiveBeforeStart = true,
                    IsSeriesParty = true,
                    StartedEpisodeId = "episode-a",
                    CurrentEpisodeId = "episode-a"
                });

            Assert.Equal(MasterPlaybackStartDisposition.ResumeCurrentEpisode, start);
            Assert.True(dormancies.IsDormant("party", "ios"));
        }

        [Fact]
        public void CrossEpisodeMasterStartSelectsThePlayNowHandoffPath()
        {
            var start = MasterPlaybackLifecyclePolicy.DecideMasterPlaybackStarted(
                new MasterPlaybackStartContext
                {
                    IsMaster = true,
                    MasterWasInactiveBeforeStart = true,
                    IsSeriesParty = true,
                    StartedEpisodeId = "episode-b",
                    CurrentEpisodeId = "episode-a",
                    HasQueuedEpisodeSelection = true
                });

            Assert.Equal(MasterPlaybackStartDisposition.SwitchEpisode, start);
        }

        [Fact]
        public void MasterDepartureCancelsOldWorkAndLeavesFollowersFree()
        {
            using var transitions = new PartyPlaybackTransitionCoordinator(
                CancellationToken.None);
            var pending = transitions.Begin("party");

            transitions.Cancel("party");
            var participantAction = PauseTransitionPolicy.Decide(
                PlaybackStateReporterRole.Participant,
                previousIsPaused: false,
                reportedIsPaused: true,
                authoritativeIsPlaying: false,
                hasActiveMaster: false);

            Assert.True(pending.Token.IsCancellationRequested);
            Assert.Equal(PlaybackStateAuthorityAction.Ignore, participantAction);
        }

    }
}
