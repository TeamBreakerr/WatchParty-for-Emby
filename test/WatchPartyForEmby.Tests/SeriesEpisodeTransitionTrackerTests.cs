using System;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class SeriesEpisodeTransitionTrackerTests
    {
        [Fact]
        public void OldEpisodeStopRetainsSessionWhileReplacementStartIsExpected()
        {
            var tracker = new SeriesEpisodeTransitionTracker();
            var now = new DateTime(2026, 8, 17, 0, 35, 15, DateTimeKind.Utc);

            tracker.ExpectStart("ios-session", "s2e2", now.AddSeconds(30));

            Assert.True(tracker.ShouldRetainSessionOnStop(
                "ios-session",
                stoppedEpisodeId: "s2e1",
                currentEpisodeId: "s2e2",
                nowUtc: now.AddSeconds(2)));
            Assert.True(tracker.IsExpectedStart(
                "ios-session",
                "s2e2",
                now.AddSeconds(5)));
        }

        [Fact]
        public void CurrentEpisodeStopOrExpiredTransitionDoesNotRetainSession()
        {
            var tracker = new SeriesEpisodeTransitionTracker();
            var now = new DateTime(2026, 8, 17, 0, 35, 15, DateTimeKind.Utc);
            tracker.ExpectStart("ios-session", "s2e2", now.AddSeconds(30));

            Assert.False(tracker.ShouldRetainSessionOnStop(
                "ios-session",
                stoppedEpisodeId: "s2e2",
                currentEpisodeId: "s2e2",
                nowUtc: now.AddSeconds(2)));
            Assert.False(tracker.ShouldRetainSessionOnStop(
                "ios-session",
                stoppedEpisodeId: "s2e1",
                currentEpisodeId: "s2e2",
                nowUtc: now.AddSeconds(31)));
        }

        [Fact]
        public void ExpectedStartIsConsumedOnlyByTheReplacementEpisode()
        {
            var tracker = new SeriesEpisodeTransitionTracker();
            var now = new DateTime(2026, 8, 17, 0, 35, 15, DateTimeKind.Utc);
            tracker.ExpectStart("ios-session", "s2e2", now.AddSeconds(30));

            Assert.False(tracker.ConsumeExpectedStart(
                "ios-session",
                "s2e1",
                now.AddSeconds(3)));
            Assert.True(tracker.ConsumeExpectedStart(
                "ios-session",
                "s2e2",
                now.AddSeconds(5)));
            Assert.False(tracker.IsExpectedStart(
                "ios-session",
                "s2e2",
                now.AddSeconds(6)));
        }

        [Fact]
        public void ClearingAPlaybackSessionRemovesItsPendingEpisodeTarget()
        {
            var tracker = new SeriesEpisodeTransitionTracker();
            var now = new DateTime(2026, 8, 17, 0, 35, 15, DateTimeKind.Utc);
            tracker.ExpectStart("ios-session", "s2e2", now.AddSeconds(30));

            Assert.True(tracker.ClearSession("ios-session"));
            Assert.False(tracker.IsExpectedStart("ios-session", "s2e2", now));
        }

        [Fact]
        public void LatestExpectedEpisodeReplacesEarlierTargetForTheSameSession()
        {
            var tracker = new SeriesEpisodeTransitionTracker();
            var now = new DateTime(2026, 8, 17, 2, 30, 0, DateTimeKind.Utc);

            tracker.ExpectStart("ios-session", "s2e2", now.AddSeconds(30));
            tracker.ExpectStart("ios-session", "s2e3", now.AddSeconds(30));

            Assert.False(tracker.IsExpectedStart(
                "ios-session",
                "s2e2",
                now.AddSeconds(1)));
            Assert.False(tracker.ConsumeExpectedStart(
                "ios-session",
                "s2e2",
                now.AddSeconds(1)));
            Assert.True(tracker.ConsumeExpectedStart(
                "ios-session",
                "s2e3",
                now.AddSeconds(1)));
        }

        [Fact]
        public void CancellingAnOlderTargetDoesNotCancelItsReplacement()
        {
            var tracker = new SeriesEpisodeTransitionTracker();
            var now = new DateTime(2026, 8, 17, 2, 45, 0, DateTimeKind.Utc);

            tracker.ExpectStart("ios-session", "s2e2", now.AddSeconds(30));
            tracker.ExpectStart("ios-session", "s2e3", now.AddSeconds(30));

            tracker.CancelExpectedStart("ios-session", "s2e2");

            Assert.True(tracker.IsExpectedStart(
                "ios-session",
                "s2e3",
                now.AddSeconds(1)));
        }

        [Fact]
        public void UnconfirmedEpisodeCommandRetriesAtMostThreeTimes()
        {
            var tracker = new SeriesEpisodeTransitionTracker();
            var now = new DateTime(2026, 8, 19, 15, 30, 0, DateTimeKind.Utc);
            var retryInterval = TimeSpan.FromSeconds(6);

            Assert.True(tracker.TryBeginCommandAttempt(
                "ios-session", "s2e2", now, retryInterval, maxAttempts: 3, out var attempt));
            Assert.Equal(1, attempt);
            Assert.False(tracker.TryBeginCommandAttempt(
                "ios-session", "s2e2", now.AddSeconds(1), retryInterval, 3, out _));

            Assert.True(tracker.TryBeginCommandAttempt(
                "ios-session", "s2e2", now.AddSeconds(6), retryInterval, 3, out attempt));
            Assert.Equal(2, attempt);
            Assert.True(tracker.TryBeginCommandAttempt(
                "ios-session", "s2e2", now.AddSeconds(12), retryInterval, 3, out attempt));
            Assert.Equal(3, attempt);
            Assert.False(tracker.TryBeginCommandAttempt(
                "ios-session", "s2e2", now.AddSeconds(18), retryInterval, 3, out _));
        }

        [Fact]
        public void ExpiredUnconfirmedCommandDoesNotStartAnotherRetryCycle()
        {
            var tracker = new SeriesEpisodeTransitionTracker();
            var now = new DateTime(2026, 8, 19, 15, 40, 0, DateTimeKind.Utc);
            var retryInterval = TimeSpan.FromSeconds(6);

            Assert.True(tracker.TryBeginCommandAttempt(
                "ios-session", "s2e2", now, retryInterval, maxAttempts: 3, out _));
            Assert.True(tracker.TryBeginCommandAttempt(
                "ios-session", "s2e2", now.AddSeconds(6), retryInterval, 3, out _));
            Assert.True(tracker.TryBeginCommandAttempt(
                "ios-session", "s2e2", now.AddSeconds(12), retryInterval, 3, out _));

            tracker.RemoveExpired(now.AddSeconds(25));

            Assert.False(tracker.TryBeginCommandAttempt(
                "ios-session", "s2e2", now.AddSeconds(30), retryInterval, 3, out _));
            Assert.True(tracker.TryBeginCommandAttempt(
                "ios-session", "s2e3", now.AddSeconds(30), retryInterval, 3, out var attempt));
            Assert.Equal(1, attempt);
        }

        [Fact]
        public void ClearingSessionAllowsSameEpisodeInANewMasterGeneration()
        {
            var tracker = new SeriesEpisodeTransitionTracker();
            var now = new DateTime(2026, 8, 19, 15, 42, 0, DateTimeKind.Utc);
            var retryInterval = TimeSpan.FromSeconds(6);

            Assert.True(tracker.TryBeginCommandAttempt(
                "ios-session", "s2e2", now, retryInterval, maxAttempts: 1, out _));
            Assert.False(tracker.TryBeginCommandAttempt(
                "ios-session", "s2e2", now.AddSeconds(6), retryInterval, 1, out _));

            Assert.True(tracker.ClearSession("ios-session"));
            Assert.True(tracker.TryBeginCommandAttempt(
                "ios-session", "s2e2", now.AddSeconds(7), retryInterval, 1, out var attempt));
            Assert.Equal(1, attempt);
        }

        [Fact]
        public void ConfirmedEpisodeStartCancelsPendingCommandRetries()
        {
            var tracker = new SeriesEpisodeTransitionTracker();
            var now = new DateTime(2026, 8, 19, 15, 45, 0, DateTimeKind.Utc);
            var retryInterval = TimeSpan.FromSeconds(6);

            Assert.True(tracker.TryBeginCommandAttempt(
                "ios-session", "s2e2", now, retryInterval, maxAttempts: 3, out _));
            Assert.True(tracker.ConsumeExpectedStart(
                "ios-session", "s2e2", now.AddSeconds(2)));
            Assert.False(tracker.IsExpectedStart(
                "ios-session", "s2e2", now.AddSeconds(3)));
        }
    }
}
