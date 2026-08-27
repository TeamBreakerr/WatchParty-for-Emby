using System;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class NaturalEpisodeAdvanceCoordinatorTests
    {
        [Theory]
        [InlineData(StoppedPlaybackLifecycleResolution.Ignored)]
        [InlineData(StoppedPlaybackLifecycleResolution.RetainedParticipant)]
        [InlineData(StoppedPlaybackLifecycleResolution.PromotedReplacementMaster)]
        public void AStopThatDidNotRemoveTheExactMasterCannotAuthorizePlayNow(
            StoppedPlaybackLifecycleResolution stopResolution)
        {
            var coordinator = new NaturalEpisodeAdvanceCoordinator();
            var now = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);

            var result = coordinator.TryBegin(
                NaturalCompletion(),
                now,
                TimeSpan.FromMinutes(1),
                () => stopResolution);

            Assert.True(result.StopResolutionAttempted);
            Assert.Equal(stopResolution, result.StopResolution);
            Assert.Null(result.Authorization);
            Assert.Equal(
                NaturalEpisodePlaybackStartDisposition.None,
                coordinator.ClassifyPlaybackStart(
                    "party",
                    "stopped-master",
                    "episode-2",
                    now.AddSeconds(1)).Disposition);
        }

        [Fact]
        public void OnlyTheCommandedMasterSessionCanConfirmTheNextEpisode()
        {
            var coordinator = new NaturalEpisodeAdvanceCoordinator();
            var now = new DateTime(2026, 8, 27, 12, 5, 0, DateTimeKind.Utc);
            var result = coordinator.TryBegin(
                NaturalCompletion(),
                now,
                TimeSpan.FromMinutes(1),
                () => StoppedPlaybackLifecycleResolution.RemovedMaster);

            Assert.NotNull(result.Authorization);
            Assert.Equal(
                NaturalEpisodePlaybackStartDisposition.RejectDifferentSession,
                coordinator.ClassifyPlaybackStart(
                    "party",
                    "other-session-of-master-user",
                    "episode-2",
                    now.AddSeconds(1)).Disposition);

            var commandedSession = coordinator.ClassifyPlaybackStart(
                "party",
                "stopped-master",
                "episode-2",
                now.AddSeconds(2));

            Assert.Equal(
                NaturalEpisodePlaybackStartDisposition.ConfirmCommandedSession,
                commandedSession.Disposition);
            Assert.Equal("episode-1-playback", commandedSession.Authorization.StoppedPlaySessionId);
            Assert.Equal("episode-2", commandedSession.Authorization.NextEpisodeId);
        }

        [Fact]
        public void ConfirmationConsumesOnlyTheExactPendingAuthorization()
        {
            var coordinator = new NaturalEpisodeAdvanceCoordinator();
            var now = new DateTime(2026, 8, 27, 12, 10, 0, DateTimeKind.Utc);
            var authorization = coordinator.TryBegin(
                    NaturalCompletion(),
                    now,
                    TimeSpan.FromMinutes(1),
                    () => StoppedPlaybackLifecycleResolution.RemovedMaster)
                .Authorization;

            Assert.False(coordinator.TryComplete(
                authorization,
                "other-session-of-master-user",
                "episode-2",
                now.AddSeconds(1),
                () => true));
            Assert.True(coordinator.IsCurrent(authorization, now.AddSeconds(2)));

            Assert.True(coordinator.TryComplete(
                authorization,
                "stopped-master",
                "episode-2",
                now.AddSeconds(3),
                () => true));
            Assert.False(coordinator.IsCurrent(authorization, now.AddSeconds(4)));
        }

        [Fact]
        public void ExpiredAuthorizationCannotMutateTheRoomDuringCommit()
        {
            var coordinator = new NaturalEpisodeAdvanceCoordinator();
            var now = new DateTime(2026, 8, 27, 12, 12, 0, DateTimeKind.Utc);
            var authorization = coordinator.TryBegin(
                    NaturalCompletion(),
                    now,
                    TimeSpan.FromSeconds(10),
                    () => StoppedPlaybackLifecycleResolution.RemovedMaster)
                .Authorization;
            var roomMutationRan = false;

            var committed = coordinator.TryComplete(
                authorization,
                "stopped-master",
                "episode-2",
                now.AddSeconds(11),
                () =>
                {
                    roomMutationRan = true;
                    return true;
                });

            Assert.False(committed);
            Assert.False(roomMutationRan);
        }

        [Fact]
        public void FailedRoomMutationDoesNotConsumeTheAuthorization()
        {
            var coordinator = new NaturalEpisodeAdvanceCoordinator();
            var now = new DateTime(2026, 8, 27, 12, 13, 0, DateTimeKind.Utc);
            var authorization = coordinator.TryBegin(
                    NaturalCompletion(),
                    now,
                    TimeSpan.FromMinutes(1),
                    () => StoppedPlaybackLifecycleResolution.RemovedMaster)
                .Authorization;

            Assert.False(coordinator.TryComplete(
                authorization,
                "stopped-master",
                "episode-2",
                now.AddSeconds(1),
                () => false));
            Assert.True(coordinator.IsCurrent(authorization, now.AddSeconds(2)));
        }

        [Fact]
        public void PlayNowRetriesAreRateLimitedAndStrictlyBounded()
        {
            var coordinator = new NaturalEpisodeAdvanceCoordinator();
            var now = new DateTime(2026, 8, 27, 12, 15, 0, DateTimeKind.Utc);
            var authorization = coordinator.TryBegin(
                    NaturalCompletion(),
                    now,
                    TimeSpan.FromMinutes(1),
                    () => StoppedPlaybackLifecycleResolution.RemovedMaster)
                .Authorization;
            var retryInterval = TimeSpan.FromSeconds(6);

            Assert.True(coordinator.TryBeginCommandAttempt(
                authorization,
                now,
                retryInterval,
                maxAttempts: 3,
                out var firstAttempt));
            Assert.Equal(1, firstAttempt);
            Assert.False(coordinator.TryBeginCommandAttempt(
                authorization,
                now.AddSeconds(5),
                retryInterval,
                maxAttempts: 3,
                out _));
            Assert.True(coordinator.TryBeginCommandAttempt(
                authorization,
                now.AddSeconds(6),
                retryInterval,
                maxAttempts: 3,
                out var secondAttempt));
            Assert.Equal(2, secondAttempt);
            Assert.True(coordinator.TryBeginCommandAttempt(
                authorization,
                now.AddSeconds(12),
                retryInterval,
                maxAttempts: 3,
                out var thirdAttempt));
            Assert.Equal(3, thirdAttempt);
            Assert.False(coordinator.TryBeginCommandAttempt(
                authorization,
                now.AddSeconds(18),
                retryInterval,
                maxAttempts: 3,
                out _));
            Assert.True(coordinator.IsCurrent(authorization, now.AddSeconds(19)));
        }

        [Theory]
        [InlineData(false, 1310, 1320, "episode-1", "episode-2")]
        [InlineData(true, 300, 1320, "episode-1", "episode-2")]
        [InlineData(true, 1310, 1320, "different-episode", "episode-2")]
        [InlineData(true, 1310, 1320, "episode-1", null)]
        [InlineData(true, 1310, 1320, "episode-1", "episode-1")]
        public void NonNaturalStopsAreResolvedNormallyWithoutOpeningTheNextEpisode(
            bool playedToCompletion,
            int positionSeconds,
            int runtimeSeconds,
            string currentEpisodeId,
            string nextEpisodeId)
        {
            var coordinator = new NaturalEpisodeAdvanceCoordinator();
            var now = new DateTime(2026, 8, 27, 12, 20, 0, DateTimeKind.Utc);
            var completion = NaturalCompletion();
            completion.PlayedToCompletion = playedToCompletion;
            completion.PositionTicks = TimeSpan.FromSeconds(positionSeconds).Ticks;
            completion.RuntimeTicks = TimeSpan.FromSeconds(runtimeSeconds).Ticks;
            completion.CurrentEpisodeId = currentEpisodeId;
            completion.NextEpisodeId = nextEpisodeId;
            var stopResolutionCalls = 0;

            var result = coordinator.TryBegin(
                completion,
                now,
                TimeSpan.FromMinutes(1),
                () =>
                {
                    stopResolutionCalls++;
                    return StoppedPlaybackLifecycleResolution.RemovedMaster;
                });

            Assert.False(result.StopResolutionAttempted);
            Assert.Equal(0, stopResolutionCalls);
            Assert.Null(result.Authorization);
        }

        private static NaturalEpisodeCompletion NaturalCompletion()
        {
            return new NaturalEpisodeCompletion
            {
                PartyId = "party",
                SessionId = "stopped-master",
                PlaySessionId = "episode-1-playback",
                CompletedEpisodeId = "episode-1",
                CurrentEpisodeId = "episode-1",
                NextEpisodeId = "episode-2",
                PlayedToCompletion = true,
                PositionTicks = TimeSpan.FromMinutes(22).Ticks
                    - TimeSpan.FromSeconds(10).Ticks,
                RuntimeTicks = TimeSpan.FromMinutes(22).Ticks
            };
        }
    }
}
