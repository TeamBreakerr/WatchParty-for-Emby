using System;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class ParticipantPositionEstimatorTests
    {
        [Fact]
        public void PlayingPositionIsProjectedBetweenSparseReports()
        {
            var reportedAt = new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);
            var participant = Participant(TimeSpan.FromSeconds(100), false, reportedAt);

            var estimated = ParticipantPositionEstimator.Estimate(
                participant,
                TimeSpan.FromSeconds(100).Ticks,
                isPaused: false,
                reportedAt.AddSeconds(8),
                TimeSpan.FromSeconds(15));

            Assert.Equal(TimeSpan.FromSeconds(108).Ticks, estimated);
        }

        [Fact]
        public void ProjectionIsCappedSoMissingReportsEventuallyTriggerCalibration()
        {
            var reportedAt = new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);
            var participant = Participant(TimeSpan.FromSeconds(100), false, reportedAt);

            var estimated = ParticipantPositionEstimator.Estimate(
                participant,
                TimeSpan.FromSeconds(100).Ticks,
                isPaused: false,
                reportedAt.AddMinutes(1),
                TimeSpan.FromSeconds(15));

            Assert.Equal(TimeSpan.FromSeconds(115).Ticks, estimated);
        }

        [Fact]
        public void PausedOrFreshlyChangedReportsAreNeverProjected()
        {
            var reportedAt = new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);
            var participant = Participant(TimeSpan.FromSeconds(100), false, reportedAt);

            Assert.Equal(
                TimeSpan.FromSeconds(100).Ticks,
                ParticipantPositionEstimator.Estimate(
                    participant,
                    TimeSpan.FromSeconds(100).Ticks,
                    isPaused: true,
                    reportedAt.AddSeconds(8),
                    TimeSpan.FromSeconds(15)));
            Assert.Equal(
                TimeSpan.FromSeconds(105).Ticks,
                ParticipantPositionEstimator.Estimate(
                    participant,
                    TimeSpan.FromSeconds(105).Ticks,
                    isPaused: false,
                    reportedAt.AddSeconds(8),
                    TimeSpan.FromSeconds(15)));
        }

        private static PartyParticipant Participant(
            TimeSpan position,
            bool isPaused,
            DateTime lastActivityAt)
        {
            return new PartyParticipant
            {
                CurrentPositionTicks = position.Ticks,
                IsPaused = isPaused,
                LastActivityAt = lastActivityAt
            };
        }
    }
}
