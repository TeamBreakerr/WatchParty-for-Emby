using System;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class PlaybackSyncCoordinatorTests
    {
        [Fact]
        public void DuplicateSeeksAreSuppressedWhileTheClientIsSettling()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);

            Assert.True(coordinator.TryBeginSeek("mac-session", TimeSpan.FromMinutes(10).Ticks, now));
            Assert.False(coordinator.TryBeginSeek("mac-session", TimeSpan.FromMinutes(10).Ticks, now));
            Assert.False(coordinator.TryBeginSeek("mac-session", TimeSpan.FromMinutes(12).Ticks, now.AddSeconds(7)));
            Assert.True(coordinator.IsSeekSettling("mac-session", now.AddSeconds(7)));

            Assert.True(coordinator.TryBeginSeek("mac-session", TimeSpan.FromMinutes(12).Ticks, now.AddSeconds(9)));
        }

        [Fact]
        public void PauseAndUnpauseEchoesAreConsumedInsteadOfBeingRebroadcast()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);

            coordinator.ExpectPauseState("ios-session", true, now);

            Assert.True(coordinator.ConsumeExpectedPauseState("ios-session", true, now.AddSeconds(1)));
            Assert.False(coordinator.ConsumeExpectedPauseState("ios-session", true, now.AddSeconds(2)));

            coordinator.ExpectPauseState("ios-session", false, now.AddSeconds(3));

            Assert.False(coordinator.ConsumeExpectedPauseState("ios-session", true, now.AddSeconds(4)));
            Assert.True(coordinator.ConsumeExpectedPauseState("ios-session", false, now.AddSeconds(4)));
        }

        [Fact]
        public void MasterClockProjectsPositionBetweenSparseClientReports()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);
            var start = TimeSpan.FromMinutes(10).Ticks;

            Assert.False(coordinator.UpdateMasterPosition(
                "party", start, true, now, TimeSpan.FromSeconds(10).Ticks));

            Assert.Equal(
                start + TimeSpan.FromSeconds(5).Ticks,
                coordinator.GetEstimatedPartyPosition("party", 0, now.AddSeconds(5)));

            Assert.False(coordinator.UpdateMasterPosition(
                "party", start + TimeSpan.FromSeconds(6).Ticks, true, now.AddSeconds(6), TimeSpan.FromSeconds(10).Ticks));

            Assert.True(coordinator.UpdateMasterPosition(
                "party", TimeSpan.FromMinutes(20).Ticks, true, now.AddSeconds(7), TimeSpan.FromSeconds(10).Ticks));
            Assert.Equal(
                TimeSpan.FromMinutes(20).Ticks,
                coordinator.GetEstimatedPartyPosition("party", 0, now.AddSeconds(7)));
        }

        [Theory]
        [InlineData(false, true, false, false, 20, 0, 10, false)]
        [InlineData(true, false, false, false, 20, 0, 10, false)]
        [InlineData(true, true, true, false, 20, 0, 10, false)]
        [InlineData(true, true, false, true, 20, 0, 10, false)]
        [InlineData(true, true, false, false, 9, 0, 10, false)]
        [InlineData(true, true, false, false, 11, 0, 10, true)]
        public void PeriodicSyncRequiresAPlayingPartyAndANonMasterOutsideTolerance(
            bool isActive,
            bool isPlaying,
            bool isWaitingRoom,
            bool isMaster,
            int sessionSeconds,
            int partySeconds,
            int toleranceSeconds,
            bool expected)
        {
            Assert.Equal(
                expected,
                PlaybackSyncCoordinator.ShouldSynchronizeParticipant(
                    isActive,
                    isPlaying,
                    isWaitingRoom,
                    isMaster,
                    TimeSpan.FromSeconds(sessionSeconds).Ticks,
                    TimeSpan.FromSeconds(partySeconds).Ticks,
                    TimeSpan.FromSeconds(toleranceSeconds).Ticks));
        }
    }
}
