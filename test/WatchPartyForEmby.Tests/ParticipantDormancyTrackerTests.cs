using System;
using System.Threading;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class ParticipantDormancyTrackerTests
    {
        [Fact]
        public void ReactivationClaimsOnlyTheMatchingDormancyRegistration()
        {
            using (var tracker = new ParticipantDormancyTracker(TimeSpan.FromSeconds(90)))
            {
                var stoppedAt = new DateTime(2026, 8, 18, 10, 0, 0, DateTimeKind.Utc);
                var registration = tracker.MarkDormant(
                    "party",
                    "ios-session",
                    "playback-1",
                    stoppedAt);

                Assert.True(tracker.TryReactivate(
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
            using (var tracker = new ParticipantDormancyTracker(TimeSpan.FromSeconds(90)))
            {
                var now = new DateTime(2026, 8, 18, 10, 0, 0, DateTimeKind.Utc);
                var oldRegistration = tracker.MarkDormant("party", "ios", "old", now);
                var newRegistration = tracker.MarkDormant("party", "ios", "new", now.AddSeconds(1));

                Assert.False(tracker.TryClaim(oldRegistration));
                Assert.True(tracker.TryClaim(newRegistration));
                Assert.False(newRegistration.CancellationToken.IsCancellationRequested);
            }
        }

        [Fact]
        public void ActivityBeforeStopDoesNotClaimDormancy()
        {
            using (var tracker = new ParticipantDormancyTracker(TimeSpan.FromSeconds(90)))
            {
                var stoppedAt = new DateTime(2026, 8, 18, 10, 0, 0, DateTimeKind.Utc);
                var registration = tracker.MarkDormant("party", "ios", "playback", stoppedAt);

                Assert.False(tracker.TryReactivate(
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
            using (var tracker = new ParticipantDormancyTracker(Timeout.InfiniteTimeSpan))
            {
                var registration = tracker.MarkDormant(
                    "party",
                    "ios",
                    "playback",
                    new DateTime(2026, 8, 18, 10, 0, 0, DateTimeKind.Utc));

                Assert.Equal(Timeout.InfiniteTimeSpan, registration.RetentionPeriod);
                Assert.True(tracker.TryReactivate(
                    "party",
                    "ios",
                    DateTime.UtcNow,
                    out var resumed));
                resumed.Dispose();
            }
        }

        [Theory]
        [InlineData(ParticipantRoomCommand.PlayNow)]
        [InlineData(ParticipantRoomCommand.Pause)]
        [InlineData(ParticipantRoomCommand.Resume)]
        [InlineData(ParticipantRoomCommand.Seek)]
        public void StoppedParticipantCannotReceivePlaybackCommands(
            ParticipantRoomCommand command)
        {
            using (var tracker = new ParticipantDormancyTracker(Timeout.InfiniteTimeSpan))
            {
                var stoppedAt = new DateTime(2026, 8, 21, 9, 0, 0, DateTimeKind.Utc);
                tracker.MarkDormant("party", "old-ios", "playback-1", stoppedAt);

                Assert.False(tracker.CanReceiveCommand(
                    "party",
                    "old-ios",
                    command));
            }
        }

        [Fact]
        public void StopRemainsAvailableForDormantParticipantTeardown()
        {
            using (var tracker = new ParticipantDormancyTracker(Timeout.InfiniteTimeSpan))
            {
                tracker.MarkDormant(
                    "party",
                    "old-ios",
                    "playback-1",
                    new DateTime(2026, 8, 21, 9, 0, 0, DateTimeKind.Utc));

                Assert.True(tracker.CanReceiveCommand(
                    "party",
                    "old-ios",
                    ParticipantRoomCommand.Stop));
            }
        }

        [Fact]
        public void AcceptedClientActivityRestoresPlaybackCommandEligibility()
        {
            using (var tracker = new ParticipantDormancyTracker(Timeout.InfiniteTimeSpan))
            {
                var stoppedAt = new DateTime(2026, 8, 21, 9, 1, 0, DateTimeKind.Utc);
                tracker.MarkDormant("party", "old-ios", "playback-1", stoppedAt);

                Assert.True(tracker.TryReactivate(
                    "party",
                    "old-ios",
                    stoppedAt.AddSeconds(1),
                    out var resumed));
                resumed.Dispose();

                Assert.True(tracker.CanReceiveCommand(
                    "party",
                    "old-ios",
                    ParticipantRoomCommand.PlayNow));
            }
        }

        [Fact]
        public void ExpectedEpisodeTransitionStopDoesNotEnterDormancy()
        {
            using (var dormancies = new ParticipantDormancyTracker(Timeout.InfiniteTimeSpan))
            {
                var transitions = new SeriesEpisodeTransitionTracker();
                var now = new DateTime(2026, 8, 21, 9, 2, 0, DateTimeKind.Utc);
                transitions.ExpectStart("ios-session", "episode-2", now.AddSeconds(30));

                var shouldRemainCommandable = transitions.ShouldRetainSessionOnStop(
                    "ios-session",
                    stoppedEpisodeId: "episode-1",
                    currentEpisodeId: "episode-2",
                    nowUtc: now.AddSeconds(1));
                if (!shouldRemainCommandable)
                {
                    dormancies.MarkDormant(
                        "party",
                        "ios-session",
                        "episode-1-playback",
                        now.AddSeconds(1));
                }

                Assert.True(shouldRemainCommandable);
                Assert.True(dormancies.CanReceiveCommand(
                    "party",
                    "ios-session",
                    ParticipantRoomCommand.PlayNow));
            }
        }

        [Fact]
        public void ActivityFromAnotherSessionCannotWakeAStoppedParticipant()
        {
            using (var tracker = new ParticipantDormancyTracker(Timeout.InfiniteTimeSpan))
            {
                var stoppedAt = new DateTime(2026, 8, 21, 9, 5, 0, DateTimeKind.Utc);
                tracker.MarkDormant("party", "old-ios", "playback-1", stoppedAt);

                Assert.False(tracker.TryReactivate(
                    "party",
                    "current-ios",
                    stoppedAt.AddSeconds(1),
                    out _));
                Assert.False(tracker.CanReceiveCommand(
                    "party",
                    "old-ios",
                    ParticipantRoomCommand.Seek));
                Assert.True(tracker.CanReceiveCommand(
                    "party",
                    "current-ios",
                    ParticipantRoomCommand.Seek));
            }
        }

        [Fact]
        public void ClearingAPartyRemovesItsDormantCommandBlock()
        {
            using (var tracker = new ParticipantDormancyTracker(Timeout.InfiniteTimeSpan))
            {
                tracker.MarkDormant(
                    "party",
                    "old-ios",
                    "playback-1",
                    new DateTime(2026, 8, 21, 9, 10, 0, DateTimeKind.Utc));

                tracker.ClearParty("party");

                Assert.True(tracker.CanReceiveCommand(
                    "party",
                    "old-ios",
                    ParticipantRoomCommand.PlayNow));
            }
        }
    }
}
