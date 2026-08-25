using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class SeriesPlaybackHandoffTrackerTests
    {
        [Fact]
        public void MasterRestartWithinTheHandoffWindowReturnsOnlyPreviouslyActiveSessions()
        {
            var now = new DateTime(2026, 8, 25, 1, 44, 0, DateTimeKind.Utc);
            var tracker = new SeriesPlaybackHandoffTracker(TimeSpan.FromSeconds(15));

            tracker.Capture(
                "party",
                "master-user",
                new[]
                {
                    Session("active-ios"),
                    Session("active-web"),
                    Session("active-ios")
                },
                now);

            var sessions = tracker.Consume(
                "party",
                "master-user",
                now.AddSeconds(7));

            Assert.Equal(
                new[] { "active-ios", "active-web" },
                sessions.Select(session => session.SessionId).OrderBy(sessionId => sessionId));
            Assert.Empty(tracker.Consume(
                "party",
                "master-user",
                now.AddSeconds(8)));
        }

        [Fact]
        public void ExpiredOrDifferentMasterCannotReviveStoppedSessions()
        {
            var now = new DateTime(2026, 8, 25, 1, 44, 0, DateTimeKind.Utc);
            var tracker = new SeriesPlaybackHandoffTracker(TimeSpan.FromSeconds(15));

            tracker.Capture(
                "party",
                "master-user",
                new[] { Session("active-ios") },
                now);

            Assert.Empty(tracker.Consume(
                "party",
                "other-master",
                now.AddSeconds(5)));
            Assert.Empty(tracker.Consume(
                "party",
                "master-user",
                now.AddSeconds(16)));
        }

        [Fact]
        public void EmptySnapshotDoesNotCreateAHandoff()
        {
            var now = new DateTime(2026, 8, 25, 1, 44, 0, DateTimeKind.Utc);
            var tracker = new SeriesPlaybackHandoffTracker(TimeSpan.FromSeconds(15));

            tracker.Capture(
                "party",
                "master-user",
                Array.Empty<SeriesPlaybackHandoffSession>(),
                now);

            Assert.Empty(tracker.Consume(
                "party",
                "master-user",
                now.AddSeconds(1)));
        }

        [Fact]
        public void ReplacementPlaybackGenerationCannotClaimAnOlderHandoff()
        {
            var now = new DateTime(2026, 8, 25, 1, 44, 0, DateTimeKind.Utc);
            var tracker = new SeriesPlaybackHandoffTracker(TimeSpan.FromSeconds(15));
            tracker.Capture(
                "party",
                "master-user",
                new[] { new SeriesPlaybackHandoffSession("ios", "old-playback") },
                now);

            var currentPlaySessionId = "replacement-playback";
            var sessions = tracker.Consume(
                "party",
                "master-user",
                now.AddSeconds(6),
                session => session.PlaySessionId == currentPlaySessionId);

            Assert.Empty(sessions);
        }

        [Fact]
        public async Task CapturedGenerationCanDispatchPlayNowAfterMirroredStopEntersDormancy()
        {
            var now = new DateTime(2026, 8, 25, 1, 44, 0, DateTimeKind.Utc);
            var tracker = new SeriesPlaybackHandoffTracker(TimeSpan.FromSeconds(15));
            using var dormancies = new ParticipantDormancyTracker(TimeSpan.FromMinutes(1));
            var commands = new DormancyAwarePlaybackCommandQueue(dormancies, 4);
            tracker.Capture(
                "party",
                "master-user",
                new[] { new SeriesPlaybackHandoffSession("ios", "episode-1-playback") },
                now);

            dormancies.MarkDormant(
                "party",
                "ios",
                "episode-1-playback",
                now.AddMilliseconds(200));
            var handoff = tracker.Consume(
                "party",
                "master-user",
                now.AddSeconds(6),
                session => session.PlaySessionId == "episode-1-playback");
            var dispatchCount = 0;

            var sent = await commands.EnqueueExplicitPlayNowAsync(
                "party",
                handoff.Single().SessionId,
                _ =>
                {
                    dispatchCount++;
                    return Task.CompletedTask;
                },
                CancellationToken.None,
                authorizationStillValid: () =>
                    handoff.Single().PlaySessionId == "episode-1-playback");

            Assert.True(dormancies.IsDormant("party", "ios"));
            Assert.True(sent);
            Assert.Equal(1, dispatchCount);
        }

        [Fact]
        public async Task FollowerThatStopsBeforeMasterCanUseOnlyItsRecentDormantGeneration()
        {
            var now = new DateTime(2026, 8, 25, 1, 44, 0, DateTimeKind.Utc);
            var handoffWindow = TimeSpan.FromSeconds(15);
            var tracker = new SeriesPlaybackHandoffTracker(handoffWindow);
            using var dormancies = new ParticipantDormancyTracker(Timeout.InfiniteTimeSpan);
            var commands = new DormancyAwarePlaybackCommandQueue(dormancies, 4);

            dormancies.MarkDormant(
                "party",
                "ios",
                "episode-1-playback",
                now);
            Assert.True(dormancies.TryGetRecentDormancy(
                "party",
                "ios",
                now.AddSeconds(1),
                handoffWindow,
                out var recentDormancy));

            tracker.Capture(
                "party",
                "master-user",
                new[]
                {
                    new SeriesPlaybackHandoffSession(
                        "ios",
                        recentDormancy.PlaySessionId,
                        recentDormancy.StoppedAtUtc.Add(handoffWindow))
                },
                now.AddSeconds(1));
            var handoff = tracker.Consume(
                "party",
                "master-user",
                now.AddSeconds(6),
                session => session.PlaySessionId == "episode-1-playback");
            var dispatchCount = 0;

            var sent = await commands.EnqueueExplicitPlayNowAsync(
                "party",
                handoff.Single().SessionId,
                _ =>
                {
                    dispatchCount++;
                    return Task.CompletedTask;
                },
                CancellationToken.None,
                authorizationStillValid: () => handoff.Single().IsValidAt(now.AddSeconds(6)));

            Assert.True(sent);
            Assert.True(dormancies.IsDormant("party", "ios"));
            Assert.Equal(1, dispatchCount);
        }

        [Fact]
        public void DormantFollowerAuthorizationExpiresFromItsOwnStopTime()
        {
            var now = new DateTime(2026, 8, 25, 1, 44, 0, DateTimeKind.Utc);
            var tracker = new SeriesPlaybackHandoffTracker(TimeSpan.FromSeconds(15));
            tracker.Capture(
                "party",
                "master-user",
                new[]
                {
                    new SeriesPlaybackHandoffSession(
                        "ios",
                        "episode-1-playback",
                        now.AddSeconds(15))
                },
                now.AddSeconds(10));

            Assert.Empty(tracker.Consume(
                "party",
                "master-user",
                now.AddSeconds(16)));
        }

        [Fact]
        public void EligibilityEvaluationRunsAfterTheTrackerLockIsReleased()
        {
            var now = new DateTime(2026, 8, 25, 1, 44, 0, DateTimeKind.Utc);
            var tracker = new SeriesPlaybackHandoffTracker(TimeSpan.FromSeconds(15));
            tracker.Capture(
                "party",
                "master-user",
                new[] { Session("ios") },
                now);

            var sessions = tracker.Consume(
                "party",
                "master-user",
                now.AddSeconds(6),
                _ =>
                {
                    var cleanup = Task.Run(() => tracker.ClearParty("party"));
                    Assert.True(cleanup.Wait(TimeSpan.FromSeconds(1)));
                    return true;
                });

            Assert.Single(sessions);
        }

        private static SeriesPlaybackHandoffSession Session(string sessionId)
        {
            return new SeriesPlaybackHandoffSession(
                sessionId,
                "playback-" + sessionId);
        }
    }
}
