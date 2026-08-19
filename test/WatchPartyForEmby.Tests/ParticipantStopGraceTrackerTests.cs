using System;
using System.Threading;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class ParticipantStopGraceTrackerTests
    {
        [Fact]
        public void ResumeClaimsOnlyTheMatchingGraceRegistration()
        {
            using (var tracker = new ParticipantStopGraceTracker(TimeSpan.FromSeconds(90)))
            {
                var stoppedAt = new DateTime(2026, 8, 18, 10, 0, 0, DateTimeKind.Utc);
                var registration = tracker.Schedule(
                    "party",
                    "ios-session",
                    "playback-1",
                    stoppedAt);

                Assert.True(tracker.TryMarkResumed(
                    "party",
                    "ios-session",
                    stoppedAt.AddSeconds(60),
                    out var resumed));
                Assert.Same(registration, resumed);
                Assert.False(tracker.TryClaim(registration));
            }
        }

        [Fact]
        public void ReplacementStopSupersedesOldRegistration()
        {
            using (var tracker = new ParticipantStopGraceTracker(TimeSpan.FromSeconds(90)))
            {
                var now = new DateTime(2026, 8, 18, 10, 0, 0, DateTimeKind.Utc);
                var oldRegistration = tracker.Schedule("party", "ios", "old", now);
                var newRegistration = tracker.Schedule("party", "ios", "new", now.AddSeconds(1));

                Assert.False(tracker.TryClaim(oldRegistration));
                Assert.True(tracker.TryClaim(newRegistration));
                Assert.False(newRegistration.CancellationToken.IsCancellationRequested);
            }
        }

        [Fact]
        public void ActivityBeforeStopDoesNotClaimGrace()
        {
            using (var tracker = new ParticipantStopGraceTracker(TimeSpan.FromSeconds(90)))
            {
                var stoppedAt = new DateTime(2026, 8, 18, 10, 0, 0, DateTimeKind.Utc);
                var registration = tracker.Schedule("party", "ios", "playback", stoppedAt);

                Assert.False(tracker.TryMarkResumed(
                    "party",
                    "ios",
                    stoppedAt.AddMilliseconds(-1),
                    out _));
                Assert.True(tracker.TryClaim(registration));
            }
        }

        [Fact]
        public void InfiniteRetentionIsAllowedForAnActiveMasterClock()
        {
            using (var tracker = new ParticipantStopGraceTracker(Timeout.InfiniteTimeSpan))
            {
                var registration = tracker.Schedule(
                    "party",
                    "ios",
                    "playback",
                    new DateTime(2026, 8, 18, 10, 0, 0, DateTimeKind.Utc));

                Assert.Equal(Timeout.InfiniteTimeSpan, registration.GracePeriod);
                Assert.True(tracker.TryMarkResumed(
                    "party",
                    "ios",
                    DateTime.UtcNow,
                    out var resumed));
                resumed.Dispose();
            }
        }
    }
}
