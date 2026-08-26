using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class MasterPlaybackLifecyclePolicyTests
    {
        [Fact]
        public void MasterStopRetiresOnlyAuthorityAndNeverStopsFollowers()
        {
            var stop = MasterPlaybackLifecyclePolicy.DecidePlaybackStopped(
                new PlaybackStopContext
                {
                    IsMaster = true,
                    IsSeriesParty = false,
                    IsExpectedEpisodeTransition = false
                });

            Assert.Equal(PlaybackStopDisposition.RetireMaster, stop.Disposition);
            Assert.True(stop.RemoveMaster);
            Assert.True(stop.RetainPlayer);
            Assert.False(stop.CaptureSeriesHandoff);
            Assert.False(stop.SendParticipantStop);
        }

        [Fact]
        public void SeriesMasterStopDuringEpisodeHandoffKeepsTheMasterRegistration()
        {
            var stop = MasterPlaybackLifecyclePolicy.DecidePlaybackStopped(
                new PlaybackStopContext
                {
                    IsMaster = true,
                    IsSeriesParty = true,
                    IsExpectedEpisodeTransition = true
                });

            Assert.Equal(
                PlaybackStopDisposition.RetainMasterForEpisodeTransition,
                stop.Disposition);
            Assert.False(stop.RemoveMaster);
            Assert.True(stop.RetainPlayer);
        }

        [Fact]
        public void ParticipantStopIsDormantUnlessItBelongsToAnEpisodeHandoff()
        {
            var dormant = MasterPlaybackLifecyclePolicy.DecidePlaybackStopped(
                new PlaybackStopContext
                {
                    IsMaster = false,
                    IsSeriesParty = true,
                    IsExpectedEpisodeTransition = false
                });
            var handoff = MasterPlaybackLifecyclePolicy.DecidePlaybackStopped(
                new PlaybackStopContext
                {
                    IsMaster = false,
                    IsSeriesParty = true,
                    IsExpectedEpisodeTransition = true
                });

            Assert.Equal(PlaybackStopDisposition.MarkParticipantDormant, dormant.Disposition);
            Assert.Equal(
                PlaybackStopDisposition.RetainParticipantForEpisodeTransition,
                handoff.Disposition);
        }

        [Fact]
        public void StopThenSameEpisodeRestartRetainsExistingAndDormantPlayers()
        {
            var stop = MasterPlaybackLifecyclePolicy.DecidePlaybackStopped(
                new PlaybackStopContext
                {
                    IsMaster = true,
                    IsSeriesParty = true,
                    IsExpectedEpisodeTransition = false
                });
            var restart = MasterPlaybackLifecyclePolicy.DecideMasterPlaybackStarted(
                new MasterPlaybackStartContext
                {
                    IsMaster = true,
                    MasterWasInactiveBeforeStart = true,
                    IsSeriesParty = true,
                    StartedEpisodeId = "episode-a",
                    CurrentEpisodeId = "episode-a"
                });

            using var dormancies = new ParticipantDormancyTracker(Timeout.InfiniteTimeSpan);
            dormancies.MarkDormant("party", "ios", "old-playback", DateTime.UtcNow);
            var followerCommands = new List<string>();
            if (stop.SendParticipantStop)
            {
                followerCommands.Add("Stop");
            }
            if (restart.SendPlayNowToParticipants)
            {
                followerCommands.Add("PlayNow");
            }

            Assert.Equal(PlaybackStopDisposition.RetireMaster, stop.Disposition);
            Assert.Equal(
                MasterPlaybackStartDisposition.ResumeCurrentEpisode,
                restart.Disposition);
            Assert.True(restart.RetainParticipantPlayers);
            Assert.True(restart.ClearHandoffAuthorizations);
            Assert.Empty(followerCommands);
            Assert.True(dormancies.IsDormant("party", "ios"));
        }

        [Fact]
        public void CrossEpisodeMasterStartRequestsPlayNowForEveryActiveFollower()
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
            var activeFollowers = new[] { "ios", "web" };
            var commands = start.SendPlayNowToParticipants
                ? activeFollowers.Select(sessionId => "PlayNow:" + sessionId).ToArray()
                : Array.Empty<string>();

            Assert.Equal(MasterPlaybackStartDisposition.SwitchEpisode, start.Disposition);
            Assert.True(start.SendPlayNowToParticipants);
            Assert.Equal(
                new[] { "PlayNow:ios", "PlayNow:web" },
                commands);
        }

        [Fact]
        public void MasterDepartureCancelsPendingWorkPreservesWaitingStateAndFreesFollowers()
        {
            using var transitions = new PartyPlaybackTransitionCoordinator(CancellationToken.None);
            var pending = transitions.Begin("party");
            var departure = MasterPlaybackLifecyclePolicy.DecideMasterDeparture(
                new MasterDepartureContext
                {
                    HasReplacementMaster = false,
                    WasWaitingRoom = false
                });

            if (departure.CancelPendingWork)
            {
                transitions.Cancel("party");
            }

            var participantAction = PauseTransitionPolicy.Decide(
                PlaybackStateReporterRole.Participant,
                previousIsPaused: false,
                reportedIsPaused: true,
                authoritativeIsPlaying: false,
                hasActiveMaster: false);

            Assert.True(pending.Token.IsCancellationRequested);
            Assert.True(departure.FreezeAuthority);
            Assert.True(departure.RetainParticipantPlayers);
            Assert.False(departure.PreserveWaitingRoom);
            Assert.Equal(PlaybackStateAuthorityAction.Ignore, participantAction);
        }

        [Fact]
        public void ReplacementMasterKeepsTheAuthoritativeClockAndWaitingRoomState()
        {
            var departure = MasterPlaybackLifecyclePolicy.DecideMasterDeparture(
                new MasterDepartureContext
                {
                    HasReplacementMaster = true,
                    WasWaitingRoom = true
                });

            Assert.True(departure.PromoteReplacementMaster);
            Assert.False(departure.FreezeAuthority);
            Assert.True(departure.PreserveWaitingRoom);
            Assert.True(departure.CancelPendingWork);
        }
    }
}
