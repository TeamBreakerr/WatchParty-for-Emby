using System.Threading.Tasks;
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
                authoritativeIsPlaying));
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
                authoritativeIsPlaying: true));
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
                authoritativeIsPlaying: false));
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
                authoritativeIsPlaying: false));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task MasterTransitionBroadcastsAndNeverUsesParticipantRestore(bool reportedIsPaused)
        {
            var participantRestoreCount = 0;
            var pauseBroadcastCount = 0;
            var resumeBroadcastCount = 0;

            await PlaybackStateAuthorityDispatcher.Dispatch(
                PlaybackStateAuthorityAction.BroadcastMaster,
                reportedIsPaused,
                () => Count(ref participantRestoreCount),
                () => Count(ref pauseBroadcastCount),
                () => Count(ref resumeBroadcastCount));

            Assert.Equal(0, participantRestoreCount);
            Assert.Equal(reportedIsPaused ? 1 : 0, pauseBroadcastCount);
            Assert.Equal(reportedIsPaused ? 0 : 1, resumeBroadcastCount);
        }

        [Fact]
        public async Task ParticipantDivergenceRestoresOnlyTheReportingParticipant()
        {
            var participantRestoreCount = 0;
            var pauseBroadcastCount = 0;
            var resumeBroadcastCount = 0;

            await PlaybackStateAuthorityDispatcher.Dispatch(
                PlaybackStateAuthorityAction.RestoreParticipant,
                reportedIsPaused: true,
                () => Count(ref participantRestoreCount),
                () => Count(ref pauseBroadcastCount),
                () => Count(ref resumeBroadcastCount));

            Assert.Equal(1, participantRestoreCount);
            Assert.Equal(0, pauseBroadcastCount);
            Assert.Equal(0, resumeBroadcastCount);
        }

        private static Task Count(ref int count)
        {
            count++;
            return Task.CompletedTask;
        }
    }
}
