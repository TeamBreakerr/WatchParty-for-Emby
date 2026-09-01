using System;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class ParticipantResumeLatencyEstimatorTests
    {
        [Fact]
        public void SlowSessionSampleDoesNotChangeFastSessionEstimate()
        {
            var estimator = CreateEstimator(smoothingFactor: 0.5);
            var sentAt = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);
            var target = TimeSpan.FromMinutes(10).Ticks;

            estimator.RecordResumeSeek("slow-ios", target, sentAt);

            Assert.False(estimator.TryRecordPlaybackProgress(
                "slow-ios",
                target,
                isPaused: false,
                sentAt + TimeSpan.FromSeconds(4),
                out _,
                out _));
            Assert.True(estimator.TryRecordPlaybackProgress(
                "slow-ios",
                target + TimeSpan.FromSeconds(1).Ticks,
                isPaused: false,
                sentAt + TimeSpan.FromSeconds(5),
                out var observedLatency,
                out var updatedEstimate));
            Assert.Equal(TimeSpan.FromSeconds(4), observedLatency);
            Assert.Equal(TimeSpan.FromSeconds(2), updatedEstimate);
            Assert.Equal(TimeSpan.Zero, estimator.GetEstimatedLatency("fast-ios"));
        }

        [Fact]
        public void EwmaSmoothsRepeatedSamplesAndClampsOutliers()
        {
            var estimator = CreateEstimator(smoothingFactor: 0.5);
            var sentAt = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);
            var target = TimeSpan.FromMinutes(10).Ticks;

            estimator.RecordResumeSeek("ios", target, sentAt);
            Assert.False(estimator.TryRecordPlaybackProgress(
                "ios",
                target,
                isPaused: false,
                sentAt + TimeSpan.FromSeconds(4),
                out _,
                out _));
            Assert.True(estimator.TryRecordPlaybackProgress(
                "ios",
                target + TimeSpan.FromSeconds(1).Ticks,
                isPaused: false,
                sentAt + TimeSpan.FromSeconds(5),
                out _,
                out var firstEstimate));

            estimator.RecordResumeSeek(
                "ios",
                target,
                sentAt + TimeSpan.FromMinutes(1));
            Assert.False(estimator.TryRecordPlaybackProgress(
                "ios",
                target,
                isPaused: false,
                sentAt + TimeSpan.FromMinutes(1) + TimeSpan.FromMilliseconds(100),
                out _,
                out _));
            Assert.True(estimator.TryRecordPlaybackProgress(
                "ios",
                target + TimeSpan.FromSeconds(1).Ticks,
                isPaused: false,
                sentAt + TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1),
                out var clampedObservation,
                out var secondEstimate));

            Assert.Equal(TimeSpan.FromSeconds(2), firstEstimate);
            Assert.Equal(TimeSpan.Zero, clampedObservation);
            Assert.Equal(TimeSpan.FromSeconds(1), secondEstimate);
        }

        [Fact]
        public void MissingAndExpiredAcknowledgementsDoNotChangeEstimate()
        {
            var estimator = CreateEstimator(smoothingFactor: 0.5);
            var sentAt = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);

            Assert.False(estimator.TryRecordPlaybackProgress(
                "ios",
                100L,
                isPaused: false,
                sentAt,
                out _,
                out _));

            estimator.RecordResumeSeek("ios", 100L, sentAt);
            Assert.False(estimator.TryRecordPlaybackProgress(
                "ios",
                100L,
                isPaused: false,
                sentAt + TimeSpan.FromSeconds(31),
                out _,
                out _));
            Assert.Equal(TimeSpan.Zero, estimator.GetEstimatedLatency("ios"));
        }

        [Fact]
        public void ClearSessionDropsPendingAndLearnedState()
        {
            var estimator = CreateEstimator(smoothingFactor: 1);
            var sentAt = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);
            var target = TimeSpan.FromSeconds(10).Ticks;

            estimator.RecordResumeSeek("ios", target, sentAt);
            Assert.False(estimator.TryRecordPlaybackProgress(
                "ios",
                target,
                isPaused: false,
                sentAt + TimeSpan.FromSeconds(3),
                out _,
                out _));
            Assert.True(estimator.TryRecordPlaybackProgress(
                "ios",
                target + TimeSpan.FromSeconds(1).Ticks,
                isPaused: false,
                sentAt + TimeSpan.FromSeconds(4),
                out _,
                out _));

            estimator.ClearSession("ios");

            Assert.Equal(TimeSpan.Zero, estimator.GetEstimatedLatency("ios"));
            Assert.False(estimator.TryRecordPlaybackProgress(
                "ios",
                target + TimeSpan.FromSeconds(2).Ticks,
                isPaused: false,
                sentAt + TimeSpan.FromSeconds(4),
                out _,
                out _));
        }

        [Fact]
        public void PausedAndStationaryReportsCannotBecomeResumeLatencySamples()
        {
            var estimator = CreateEstimator(smoothingFactor: 1);
            var sentAt = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);
            var target = TimeSpan.FromMinutes(10).Ticks;
            estimator.RecordResumeSeek("ios", target, sentAt);

            Assert.False(estimator.TryRecordPlaybackProgress(
                "ios",
                target,
                isPaused: true,
                sentAt + TimeSpan.FromSeconds(2),
                out _,
                out _));
            Assert.False(estimator.TryRecordPlaybackProgress(
                "ios",
                target,
                isPaused: false,
                sentAt + TimeSpan.FromSeconds(3),
                out _,
                out _));
            Assert.False(estimator.TryRecordPlaybackProgress(
                "ios",
                target,
                isPaused: false,
                sentAt + TimeSpan.FromSeconds(4),
                out _,
                out _));
            Assert.Equal(TimeSpan.Zero, estimator.GetEstimatedLatency("ios"));
        }

        [Fact]
        public void PositionBehindTheCommandedTargetIsIgnoredAsAStalePlayerReport()
        {
            var estimator = CreateEstimator(smoothingFactor: 1);
            var sentAt = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);
            var target = TimeSpan.FromMinutes(10).Ticks;
            estimator.RecordResumeSeek("ios", target, sentAt);

            Assert.False(estimator.TryRecordPlaybackProgress(
                "ios",
                TimeSpan.FromMinutes(2).Ticks,
                isPaused: false,
                sentAt + TimeSpan.FromSeconds(3),
                out _,
                out _));
            Assert.False(estimator.TryRecordPlaybackProgress(
                "ios",
                TimeSpan.FromMinutes(20).Ticks,
                isPaused: false,
                sentAt + TimeSpan.FromSeconds(3),
                out _,
                out _));
            Assert.False(estimator.TryRecordPlaybackProgress(
                "ios",
                target,
                isPaused: false,
                sentAt + TimeSpan.FromSeconds(4),
                out _,
                out _));
            Assert.True(estimator.TryRecordPlaybackProgress(
                "ios",
                target + TimeSpan.FromSeconds(1).Ticks,
                isPaused: false,
                sentAt + TimeSpan.FromSeconds(5),
                out var observedLatency,
                out _));
            Assert.Equal(TimeSpan.FromSeconds(4), observedLatency);
        }

        private static ParticipantResumeLatencyEstimator CreateEstimator(
            double smoothingFactor)
        {
            return new ParticipantResumeLatencyEstimator(
                initialEstimate: TimeSpan.Zero,
                minimumEstimate: TimeSpan.Zero,
                maximumEstimate: TimeSpan.FromSeconds(6),
                sampleTimeout: TimeSpan.FromSeconds(30),
                smoothingFactor: smoothingFactor);
        }
    }
}
