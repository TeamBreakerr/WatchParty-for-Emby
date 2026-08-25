using System;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class PlaybackGenerationCandidateTrackerTests
    {
        private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan ConfirmationGap = TimeSpan.FromSeconds(12);

        [Fact]
        public void StableForeignGenerationIsConfirmedOnlyAfterTheCurrentGenerationIsQuiet()
        {
            var tracker = Tracker();
            var now = new DateTime(2026, 8, 25, 14, 0, 0, DateTimeKind.Utc);

            Assert.False(Observe(
                tracker,
                "replacement",
                currentLastActivityAtUtc: now,
                observedAtUtc: now.AddSeconds(1),
                positionSeconds: 31));
            Assert.False(Observe(
                tracker,
                "replacement",
                currentLastActivityAtUtc: now,
                observedAtUtc: now.AddSeconds(4),
                positionSeconds: 34));
            Assert.True(Observe(
                tracker,
                "replacement",
                currentLastActivityAtUtc: now,
                observedAtUtc: now.AddSeconds(9),
                positionSeconds: 39));
        }

        [Fact]
        public void OneForeignReportCannotReplaceTheRegisteredGeneration()
        {
            var tracker = Tracker();
            var now = new DateTime(2026, 8, 25, 14, 0, 0, DateTimeKind.Utc);

            Assert.False(Observe(
                tracker,
                "replacement",
                currentLastActivityAtUtc: now,
                observedAtUtc: now.AddSeconds(5),
                positionSeconds: 35));
        }

        [Fact]
        public void AlternatingForeignGenerationsCannotConfirmEachOther()
        {
            var tracker = Tracker();
            var now = new DateTime(2026, 8, 25, 14, 0, 0, DateTimeKind.Utc);

            Assert.False(Observe(tracker, "replacement-a", now, now.AddSeconds(4), 34));
            Assert.False(Observe(tracker, "replacement-b", now, now.AddSeconds(6), 36));
            Assert.False(Observe(tracker, "replacement-a", now, now.AddSeconds(8), 38));
        }

        [Fact]
        public void CurrentGenerationActivityClearsAPendingReplacement()
        {
            var tracker = Tracker();
            var now = new DateTime(2026, 8, 25, 14, 0, 0, DateTimeKind.Utc);

            Assert.False(Observe(tracker, "replacement", now, now.AddSeconds(4), 34));
            tracker.Reset("party", "web-session");
            Assert.False(Observe(tracker, "replacement", now, now.AddSeconds(6), 36));
            Assert.True(Observe(tracker, "replacement", now, now.AddSeconds(8), 38));
        }

        [Fact]
        public void ImplausiblePositionJumpRestartsConfirmation()
        {
            var tracker = Tracker();
            var now = new DateTime(2026, 8, 25, 14, 0, 0, DateTimeKind.Utc);

            Assert.False(Observe(tracker, "replacement", now, now.AddSeconds(4), 100));
            Assert.False(Observe(tracker, "replacement", now, now.AddSeconds(6), 50));
            Assert.True(Observe(tracker, "replacement", now, now.AddSeconds(8), 52));
        }

        [Fact]
        public void ConfirmationOutsideTheBoundedGapStartsOver()
        {
            var tracker = Tracker();
            var now = new DateTime(2026, 8, 25, 14, 0, 0, DateTimeKind.Utc);

            Assert.False(Observe(tracker, "replacement", now, now.AddSeconds(4), 34));
            Assert.False(Observe(tracker, "replacement", now, now.AddSeconds(20), 50));
        }

        private static PlaybackGenerationCandidateTracker Tracker()
        {
            return new PlaybackGenerationCandidateTracker(
                QuietPeriod,
                ConfirmationGap,
                requiredConfirmations: 2,
                positionTolerance: TimeSpan.FromSeconds(5));
        }

        private static bool Observe(
            PlaybackGenerationCandidateTracker tracker,
            string candidatePlaySessionId,
            DateTime currentLastActivityAtUtc,
            DateTime observedAtUtc,
            double positionSeconds)
        {
            return tracker.Observe(
                "party",
                "web-session",
                "registered",
                candidatePlaySessionId,
                currentLastActivityAtUtc,
                observedAtUtc,
                TimeSpan.FromSeconds(positionSeconds).Ticks,
                isPaused: false);
        }
    }
}
