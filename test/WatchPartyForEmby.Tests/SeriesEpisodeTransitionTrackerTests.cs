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
    }
}
