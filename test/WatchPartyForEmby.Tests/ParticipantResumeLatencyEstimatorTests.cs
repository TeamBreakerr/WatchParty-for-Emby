using System;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class ParticipantResumeLatencyEstimatorTests
    {
        private const string RoomA = "party-berserk";
        private const string RoomB = "party-mushoku";
        private static readonly DateTime Origin =
            new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);
        private static readonly long Target = TimeSpan.FromMinutes(10).Ticks;

        [Fact]
        public void TheFirstSampleInARoomIsAdoptedAtFaceValue()
        {
            // With no measurement for a room yet there is nothing to smooth towards.
            // Blending the first observation into a zero prior compensated the next
            // resume by half of what it needed, which is why the first resumes after
            // a room switch were visibly short.
            var estimator = CreateEstimator(smoothingFactor: 0.5);

            var estimate = ObserveResume(
                estimator, RoomA, "ios", Origin, TimeSpan.FromSeconds(4));

            Assert.Equal(TimeSpan.FromSeconds(4), estimate);
            Assert.Equal(
                TimeSpan.FromSeconds(4),
                estimator.GetEstimatedLatency(RoomA, "ios"));
        }

        [Fact]
        public void LaterSamplesInTheSameRoomAreSmoothedAndClamped()
        {
            var estimator = CreateEstimator(smoothingFactor: 0.5);
            ObserveResume(estimator, RoomA, "ios", Origin, TimeSpan.FromSeconds(4));

            var later = Origin + TimeSpan.FromMinutes(1);
            estimator.RecordResumeSeek(RoomA, "ios", Target, later);
            Assert.False(estimator.TryRecordPlaybackProgress(
                "ios",
                Target,
                isPaused: false,
                later + TimeSpan.FromMilliseconds(100),
                out _,
                out _));
            Assert.True(estimator.TryRecordPlaybackProgress(
                "ios",
                Target + TimeSpan.FromSeconds(1).Ticks,
                isPaused: false,
                later + TimeSpan.FromSeconds(1),
                out var observed,
                out var estimate));

            // An instant resume is a real observation, not an outlier to discard; it
            // pulls the estimate down by the smoothing factor rather than replacing it.
            Assert.Equal(TimeSpan.Zero, observed);
            Assert.Equal(TimeSpan.FromSeconds(2), estimate);
        }

        [Fact]
        public void ARisingLatencyIsTrackedFasterThanAFallingOne()
        {
            // Within one sitting the measurements climb - 1441, 1025, 3247, 3732 ms
            // were measured on one device in fourteen minutes - and a symmetric
            // average is structurally half a step behind a signal that climbs. That
            // lag is the residual the viewer actually feels on resume.
            var estimator = CreateEstimator(
                smoothingFactor: 0.25,
                risingSmoothingFactor: 0.75);

            var first = ObserveResume(
                estimator, RoomA, "ios", Origin, TimeSpan.FromSeconds(1));
            var risen = ObserveResume(
                estimator,
                RoomA,
                "ios",
                Origin + TimeSpan.FromMinutes(1),
                TimeSpan.FromSeconds(3));
            var fallen = ObserveResume(
                estimator,
                RoomA,
                "ios",
                Origin + TimeSpan.FromMinutes(2),
                TimeSpan.FromMilliseconds(500));

            Assert.Equal(TimeSpan.FromSeconds(1), first);

            // Most of a slower measurement is adopted at once: the client got slower
            // and tends to stay that way.
            Assert.Equal(TimeSpan.FromSeconds(2.5), risen);

            // One lucky fast resume must not discard the knowledge that this client
            // can be slow, so it is only taken a quarter of the way.
            Assert.Equal(TimeSpan.FromSeconds(2), fallen);
        }

        [Fact]
        public void RoomsLearnIndependently()
        {
            // Resume latency is dominated by what the client must do to produce the
            // first frame, and that is a property of the media: a title it direct-plays
            // resumes in half a second, one that restarts a transcode in two or three.
            // A room is bound to its content, so one room's measurement must not drag
            // another's estimate around.
            var estimator = CreateEstimator(smoothingFactor: 0.5);

            ObserveResume(estimator, RoomA, "ios", Origin, TimeSpan.FromSeconds(4));
            ObserveResume(
                estimator,
                RoomB,
                "ios",
                Origin + TimeSpan.FromMinutes(5),
                TimeSpan.FromSeconds(1));

            Assert.Equal(
                TimeSpan.FromSeconds(4),
                estimator.GetEstimatedLatency(RoomA, "ios"));
            Assert.Equal(
                TimeSpan.FromSeconds(1),
                estimator.GetEstimatedLatency(RoomB, "ios"));
        }

        [Fact]
        public void AnUnmeasuredRoomBorrowsWhatTheSessionLearnedElsewhere()
        {
            var estimator = CreateEstimator(smoothingFactor: 0.5);
            ObserveResume(estimator, RoomA, "ios", Origin, TimeSpan.FromSeconds(4));

            // Better an opening guess from the same device than assuming it resumes
            // instantly, which is what an unseeded room used to assume.
            Assert.Equal(
                TimeSpan.FromSeconds(4),
                estimator.GetEstimatedLatency(RoomB, "ios"));

            // A device that has never been measured has nothing to borrow.
            Assert.Equal(
                TimeSpan.Zero,
                estimator.GetEstimatedLatency(RoomA, "another-device"));
        }

        [Fact]
        public void LeavingARoomDropsThePendingObservationButKeepsWhatItLearned()
        {
            // Switching rooms removes the participant. Discarding the measurement with
            // it meant the first resume in every room was uncompensated - and switching
            // rooms is exactly when people press play.
            var estimator = CreateEstimator(smoothingFactor: 0.5);
            ObserveResume(estimator, RoomA, "ios", Origin, TimeSpan.FromSeconds(4));

            var pendingAt = Origin + TimeSpan.FromMinutes(1);
            estimator.RecordResumeSeek(RoomA, "ios", Target, pendingAt);
            estimator.ClearSession("ios");

            Assert.Equal(
                TimeSpan.FromSeconds(4),
                estimator.GetEstimatedLatency(RoomA, "ios"));
            Assert.False(estimator.TryRecordPlaybackProgress(
                "ios",
                Target + TimeSpan.FromSeconds(2).Ticks,
                isPaused: false,
                pendingAt + TimeSpan.FromSeconds(4),
                out _,
                out _));
        }

        [Fact]
        public void ClearDiscardsEverything()
        {
            var estimator = CreateEstimator(smoothingFactor: 0.5);
            ObserveResume(estimator, RoomA, "ios", Origin, TimeSpan.FromSeconds(4));

            estimator.Clear();

            Assert.Equal(
                TimeSpan.Zero,
                estimator.GetEstimatedLatency(RoomA, "ios"));
        }

        [Fact]
        public void MissingAndExpiredAcknowledgementsDoNotChangeEstimate()
        {
            var estimator = CreateEstimator(smoothingFactor: 0.5);

            Assert.False(estimator.TryRecordPlaybackProgress(
                "ios",
                100L,
                isPaused: false,
                Origin,
                out _,
                out _));

            estimator.RecordResumeSeek(RoomA, "ios", 100L, Origin);
            Assert.False(estimator.TryRecordPlaybackProgress(
                "ios",
                100L,
                isPaused: false,
                Origin + TimeSpan.FromSeconds(31),
                out _,
                out _));
            Assert.Equal(TimeSpan.Zero, estimator.GetEstimatedLatency(RoomA, "ios"));
        }

        [Fact]
        public void PausedAndStationaryReportsCannotBecomeResumeLatencySamples()
        {
            var estimator = CreateEstimator(smoothingFactor: 1);
            estimator.RecordResumeSeek(RoomA, "ios", Target, Origin);

            Assert.False(estimator.TryRecordPlaybackProgress(
                "ios",
                Target,
                isPaused: true,
                Origin + TimeSpan.FromSeconds(2),
                out _,
                out _));
            Assert.False(estimator.TryRecordPlaybackProgress(
                "ios",
                Target,
                isPaused: false,
                Origin + TimeSpan.FromSeconds(3),
                out _,
                out _));
            Assert.False(estimator.TryRecordPlaybackProgress(
                "ios",
                Target,
                isPaused: false,
                Origin + TimeSpan.FromSeconds(4),
                out _,
                out _));
            Assert.Equal(TimeSpan.Zero, estimator.GetEstimatedLatency(RoomA, "ios"));
        }

        [Fact]
        public void PositionBehindTheCommandedTargetIsIgnoredAsAStalePlayerReport()
        {
            var estimator = CreateEstimator(smoothingFactor: 1);
            estimator.RecordResumeSeek(RoomA, "ios", Target, Origin);

            Assert.False(estimator.TryRecordPlaybackProgress(
                "ios",
                TimeSpan.FromMinutes(2).Ticks,
                isPaused: false,
                Origin + TimeSpan.FromSeconds(3),
                out _,
                out _));
            Assert.False(estimator.TryRecordPlaybackProgress(
                "ios",
                TimeSpan.FromMinutes(20).Ticks,
                isPaused: false,
                Origin + TimeSpan.FromSeconds(3),
                out _,
                out _));
            Assert.False(estimator.TryRecordPlaybackProgress(
                "ios",
                Target,
                isPaused: false,
                Origin + TimeSpan.FromSeconds(4),
                out _,
                out _));
            Assert.True(estimator.TryRecordPlaybackProgress(
                "ios",
                Target + TimeSpan.FromSeconds(1).Ticks,
                isPaused: false,
                Origin + TimeSpan.FromSeconds(5),
                out var observedLatency,
                out _));
            Assert.Equal(TimeSpan.FromSeconds(4), observedLatency);
        }

        /// <summary>
        /// Drives one complete resume observation and returns the resulting estimate.
        /// The player reports the commanded position first - which only establishes a
        /// baseline - and then a position one second further on, so the elapsed time
        /// minus the media it advanced is the latency being measured.
        /// </summary>
        private static TimeSpan ObserveResume(
            ParticipantResumeLatencyEstimator estimator,
            string partyId,
            string sessionId,
            DateTime sentAtUtc,
            TimeSpan latency)
        {
            estimator.RecordResumeSeek(partyId, sessionId, Target, sentAtUtc);
            Assert.False(estimator.TryRecordPlaybackProgress(
                sessionId,
                Target,
                isPaused: false,
                sentAtUtc + latency,
                out _,
                out _));
            Assert.True(estimator.TryRecordPlaybackProgress(
                sessionId,
                Target + TimeSpan.FromSeconds(1).Ticks,
                isPaused: false,
                sentAtUtc + latency + TimeSpan.FromSeconds(1),
                out var observed,
                out var estimate));
            Assert.Equal(latency, observed);
            return estimate;
        }

        private static ParticipantResumeLatencyEstimator CreateEstimator(
            double smoothingFactor,
            double? risingSmoothingFactor = null)
        {
            return new ParticipantResumeLatencyEstimator(
                initialEstimate: TimeSpan.Zero,
                minimumEstimate: TimeSpan.Zero,
                maximumEstimate: TimeSpan.FromSeconds(6),
                sampleTimeout: TimeSpan.FromSeconds(30),
                smoothingFactor: smoothingFactor,
                risingSmoothingFactor: risingSmoothingFactor);
        }
    }
}
