using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class PauseTransitionPolicyTests
    {
        [Theory]
        [InlineData(true, true)]
        [InlineData(false, false)]
        public void ParticipantPlaybackStateCanOnlyBeRestoredToTheMaster(
            bool reportedIsPaused,
            bool authoritativeIsPlaying)
        {
            Assert.Equal(
                PlaybackStateAuthorityAction.RestoreParticipant,
                PauseTransitionPolicy.Decide(
                PlaybackStateReporterRole.Participant,
                previousIsPaused: true,
                reportedIsPaused,
                authoritativeIsPlaying,
                hasActiveMaster: true));
        }

        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public void MasterTransitionsAreTheOnlyRoomWidePlaybackControls(
            bool previousIsPaused,
            bool reportedIsPaused)
        {
            Assert.Equal(
                PlaybackStateAuthorityAction.BroadcastMaster,
                PauseTransitionPolicy.Decide(
                PlaybackStateReporterRole.Master,
                previousIsPaused,
                reportedIsPaused,
                authoritativeIsPlaying: true,
                hasActiveMaster: true));
        }

        [Fact]
        public void StaleMasterHeartbeatCannotChangeTheRoom()
        {
            Assert.Equal(
                PlaybackStateAuthorityAction.Ignore,
                PauseTransitionPolicy.Decide(
                PlaybackStateReporterRole.Master,
                previousIsPaused: false,
                reportedIsPaused: false,
                authoritativeIsPlaying: false,
                hasActiveMaster: true));
        }

        [Fact]
        public void ParticipantStateMatchingTheMasterNeedsNoCommand()
        {
            Assert.Equal(
                PlaybackStateAuthorityAction.Ignore,
                PauseTransitionPolicy.Decide(
                PlaybackStateReporterRole.Participant,
                previousIsPaused: true,
                reportedIsPaused: true,
                authoritativeIsPlaying: false,
                hasActiveMaster: true));
        }

        [Theory]
        [InlineData(true, true)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(false, false)]
        public void ParticipantIsFreeWhenMasterIsOffline(
            bool reportedIsPaused,
            bool authoritativeIsPlaying)
        {
            Assert.Equal(
                PlaybackStateAuthorityAction.Ignore,
                PauseTransitionPolicy.Decide(
                    PlaybackStateReporterRole.Participant,
                    previousIsPaused: !reportedIsPaused,
                    reportedIsPaused,
                    authoritativeIsPlaying,
                    hasActiveMaster: false));
        }

    }
}
