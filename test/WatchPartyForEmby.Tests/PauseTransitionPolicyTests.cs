using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class PauseTransitionPolicyTests
    {
        [Fact]
        public void AnyoneModeRecoversParticipantTransitionWhenSessionCacheIsStale()
        {
            Assert.True(PauseTransitionPolicy.ShouldHandle(
                isMaster: false,
                isWaitingRoom: false,
                isInitialParticipantReport: false,
                isSyntheticEcho: false,
                previousIsPaused: true,
                reportedIsPaused: true,
                authoritativeIsPlaying: true));
        }

        [Fact]
        public void HostModeStillProcessesStaleStateSoTheCoordinatorCanRejectIt()
        {
            Assert.True(PauseTransitionPolicy.ShouldHandle(
                isMaster: false,
                isWaitingRoom: false,
                isInitialParticipantReport: false,
                isSyntheticEcho: false,
                previousIsPaused: true,
                reportedIsPaused: true,
                authoritativeIsPlaying: true));
        }

        [Fact]
        public void StaleMasterHeartbeatCannotUndoParticipantPause()
        {
            Assert.False(PauseTransitionPolicy.ShouldHandle(
                isMaster: true,
                isWaitingRoom: false,
                isInitialParticipantReport: false,
                isSyntheticEcho: false,
                previousIsPaused: false,
                reportedIsPaused: false,
                authoritativeIsPlaying: false));
        }

        [Fact]
        public void GenuineTransitionIsHandledEvenWhenItMatchesTheAuthoritativeState()
        {
            Assert.True(PauseTransitionPolicy.ShouldHandle(
                isMaster: false,
                isWaitingRoom: false,
                isInitialParticipantReport: false,
                isSyntheticEcho: false,
                previousIsPaused: false,
                reportedIsPaused: true,
                authoritativeIsPlaying: true));
        }

        [Fact]
        public void InitialAndSyntheticReportsNeverControlTheRoom()
        {
            Assert.False(PauseTransitionPolicy.ShouldHandle(
                isMaster: false,
                isWaitingRoom: false,
                isInitialParticipantReport: true,
                isSyntheticEcho: false,
                previousIsPaused: false,
                reportedIsPaused: true,
                authoritativeIsPlaying: true));
            Assert.False(PauseTransitionPolicy.ShouldHandle(
                isMaster: false,
                isWaitingRoom: false,
                isInitialParticipantReport: false,
                isSyntheticEcho: true,
                previousIsPaused: false,
                reportedIsPaused: true,
                authoritativeIsPlaying: true));
        }
    }
}
