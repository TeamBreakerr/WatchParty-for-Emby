using System;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class ParticipantResumeLatencyEstimatorTests
    {
        private static readonly PlaybackSyncCoordinator ExpectationSource =
            new PlaybackSyncCoordinator();

        [Fact]
        public void SlowSessionSampleDoesNotChangeFastSessionEstimate()
        {
            var estimator = CreateEstimator(smoothingFactor: 0.5);
            var sentAt = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);
            var expectation = NewExpectation("slow-ios");

            estimator.RecordResumeCommand("slow-ios", expectation, sentAt);

            Assert.True(estimator.TryRecordResumeAcknowledgement(
                "slow-ios",
                expectation,
                sentAt + TimeSpan.FromSeconds(4),
                out var observedLatency,
                out var updatedEstimate));
            Assert.Equal(TimeSpan.FromSeconds(4), observedLatency);
            Assert.Equal(TimeSpan.FromMilliseconds(2125), updatedEstimate);
            Assert.Equal(
                TimeSpan.FromMilliseconds(250),
                estimator.GetEstimatedLatency("fast-ios"));
        }

        [Fact]
        public void EwmaSmoothsRepeatedSamplesAndClampsOutliers()
        {
            var estimator = CreateEstimator(smoothingFactor: 0.5);
            var sentAt = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);
            var firstExpectation = NewExpectation("ios");

            estimator.RecordResumeCommand("ios", firstExpectation, sentAt);
            Assert.True(estimator.TryRecordResumeAcknowledgement(
                "ios",
                firstExpectation,
                sentAt + TimeSpan.FromSeconds(4),
                out _,
                out var firstEstimate));

            var secondExpectation = NewExpectation("ios");
            estimator.RecordResumeCommand(
                "ios",
                secondExpectation,
                sentAt + TimeSpan.FromMinutes(1));
            Assert.True(estimator.TryRecordResumeAcknowledgement(
                "ios",
                secondExpectation,
                sentAt + TimeSpan.FromMinutes(1) + TimeSpan.FromMilliseconds(10),
                out var clampedObservation,
                out var secondEstimate));

            Assert.Equal(TimeSpan.FromMilliseconds(2125), firstEstimate);
            Assert.Equal(TimeSpan.FromMilliseconds(100), clampedObservation);
            Assert.Equal(TimeSpan.FromMilliseconds(1112.5), secondEstimate);
        }

        [Fact]
        public void MissingAndExpiredAcknowledgementsDoNotChangeEstimate()
        {
            var estimator = CreateEstimator(smoothingFactor: 0.5);
            var sentAt = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);

            Assert.False(estimator.TryRecordResumeAcknowledgement(
                "ios",
                NewExpectation("ios"),
                sentAt,
                out _,
                out _));

            var expectation = NewExpectation("ios");
            estimator.RecordResumeCommand("ios", expectation, sentAt);
            Assert.False(estimator.TryRecordResumeAcknowledgement(
                "ios",
                expectation,
                sentAt + TimeSpan.FromSeconds(31),
                out _,
                out _));
            Assert.Equal(
                TimeSpan.FromMilliseconds(250),
                estimator.GetEstimatedLatency("ios"));
        }

        [Fact]
        public void ClearSessionDropsPendingAndLearnedState()
        {
            var estimator = CreateEstimator(smoothingFactor: 1);
            var sentAt = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);
            var expectation = NewExpectation("ios");

            estimator.RecordResumeCommand("ios", expectation, sentAt);
            Assert.True(estimator.TryRecordResumeAcknowledgement(
                "ios",
                expectation,
                sentAt + TimeSpan.FromSeconds(3),
                out _,
                out _));

            estimator.ClearSession("ios");

            Assert.Equal(
                TimeSpan.FromMilliseconds(250),
                estimator.GetEstimatedLatency("ios"));
            Assert.False(estimator.TryRecordResumeAcknowledgement(
                "ios",
                expectation,
                sentAt + TimeSpan.FromSeconds(4),
                out _,
                out _));
        }

        [Fact]
        public void StaleAcknowledgementCannotCompleteANewerObservation()
        {
            var estimator = CreateEstimator(smoothingFactor: 1);
            var sentAt = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);
            var staleExpectation = NewExpectation("ios");
            var currentExpectation = NewExpectation("ios");

            estimator.RecordResumeCommand("ios", staleExpectation, sentAt);
            estimator.RecordResumeCommand(
                "ios",
                currentExpectation,
                sentAt + TimeSpan.FromSeconds(1));
            estimator.CancelPendingResume("ios", staleExpectation);

            Assert.False(estimator.TryRecordResumeAcknowledgement(
                "ios",
                staleExpectation,
                sentAt + TimeSpan.FromSeconds(2),
                out _,
                out _));
            Assert.Equal(
                TimeSpan.FromMilliseconds(250),
                estimator.GetEstimatedLatency("ios"));

            Assert.True(estimator.TryRecordResumeAcknowledgement(
                "ios",
                currentExpectation,
                sentAt + TimeSpan.FromSeconds(4),
                out var observedLatency,
                out var updatedEstimate));
            Assert.Equal(TimeSpan.FromSeconds(3), observedLatency);
            Assert.Equal(TimeSpan.FromSeconds(3), updatedEstimate);
        }

        [Fact]
        public void CoalescedUnpauseEchoMatchesTheCurrentlyObservedExpectation()
        {
            var syncCoordinator = new PlaybackSyncCoordinator();
            var estimator = CreateEstimator(smoothingFactor: 1);
            var sentAt = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);
            var firstExpectation = syncCoordinator.ExpectPauseState(
                "ios",
                isPaused: false,
                sentAt);
            var secondExpectation = syncCoordinator.ExpectPauseState(
                "ios",
                isPaused: false,
                sentAt + TimeSpan.FromMilliseconds(10));
            estimator.RecordResumeCommand(
                "ios",
                secondExpectation,
                sentAt + TimeSpan.FromMilliseconds(20));

            var classification = syncCoordinator.ClassifyInboundPauseState(
                "ios",
                previousIsPaused: true,
                reportedIsPaused: false,
                sentAt + TimeSpan.FromSeconds(3));

            Assert.True(classification.IsExpectedCommandEcho);
            Assert.Equal(2, classification.MatchedExpectations.Count);
            Assert.True(estimator.TryRecordResumeAcknowledgement(
                "ios",
                classification.MatchedExpectations,
                sentAt + TimeSpan.FromSeconds(3),
                out var observedLatency,
                out var updatedEstimate));
            Assert.Equal(TimeSpan.FromMilliseconds(2980), observedLatency);
            Assert.Equal(TimeSpan.FromMilliseconds(2980), updatedEstimate);
            Assert.NotEqual(firstExpectation, secondExpectation);
        }

        private static ParticipantResumeLatencyEstimator CreateEstimator(
            double smoothingFactor)
        {
            return new ParticipantResumeLatencyEstimator(
                initialEstimate: TimeSpan.FromMilliseconds(250),
                minimumEstimate: TimeSpan.FromMilliseconds(100),
                maximumEstimate: TimeSpan.FromSeconds(6),
                sampleTimeout: TimeSpan.FromSeconds(30),
                smoothingFactor: smoothingFactor);
        }

        private static PauseStateExpectationToken NewExpectation(string sessionId)
        {
            return ExpectationSource.ExpectPauseState(
                sessionId,
                isPaused: false,
                DateTime.UtcNow);
        }
    }
}
