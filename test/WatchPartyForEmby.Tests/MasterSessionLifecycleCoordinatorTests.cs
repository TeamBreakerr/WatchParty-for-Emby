using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class MasterSessionLifecycleCoordinatorTests
    {
        [Fact]
        public async Task ReplacementMasterWaitsForStoppedMasterCleanup()
        {
            var registry = new PartySessionRegistry();
            using var dormancies = new ParticipantDormancyTracker(
                Timeout.InfiniteTimeSpan);
            var coordinator = new MasterSessionLifecycleCoordinator(
                registry,
                dormancies);
            var now = new DateTime(2026, 8, 27, 2, 0, 0, DateTimeKind.Utc);
            registry.AddOrUpdate(
                "party",
                "master-a",
                Participant("master", "master-a", "play-a", now));
            registry.AddOrUpdate(
                "party",
                "master-b",
                Participant("master", "master-b", "play-b", now.AddSeconds(1)));
            Assert.True(registry.SetMasterSession("party", "master-a"));

            var masterRemoved = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var allowCleanupToFinish = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var replacementAttempted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var cleanupCompleted = false;

            var stop = Task.Run(() => coordinator.Execute(
                "party",
                () =>
                {
                    Assert.Equal(
                        PlaybackStopRegistryResolution.RemovedMaster,
                        registry.ResolvePlaybackStop(
                            "party",
                            "master-a",
                            "play-a",
                            out _));
                    masterRemoved.TrySetResult(true);
                    allowCleanupToFinish.Task.GetAwaiter().GetResult();
                    cleanupCompleted = true;
                }));

            await masterRemoved.Task;
            var replacement = Task.Run(() =>
            {
                replacementAttempted.TrySetResult(true);
                var registered = false;
                coordinator.Execute(
                    "party",
                    () =>
                    {
                        Assert.True(cleanupCompleted);
                        registered = registry.SetMasterSession(
                            "party",
                            "master-b");
                    });
                return registered;
            });

            await replacementAttempted.Task;
            Assert.False(replacement.IsCompleted);

            allowCleanupToFinish.TrySetResult(true);
            await stop;
            var replacementRegistered = await replacement;

            Assert.True(replacementRegistered);
            Assert.Equal("master-b", registry.GetMasterSession("party"));
        }

        [Fact]
        public async Task PromotionAfterParticipantStopClearsDormancyInOrder()
        {
            var registry = new PartySessionRegistry();
            using var dormancies = new ParticipantDormancyTracker(
                Timeout.InfiniteTimeSpan);
            var coordinator = new MasterSessionLifecycleCoordinator(
                registry,
                dormancies);
            var now = new DateTime(2026, 8, 27, 2, 10, 0, DateTimeKind.Utc);
            registry.AddOrUpdate(
                "party",
                "candidate",
                Participant("master", "candidate", "play-a", now));
            var stopResolved = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var allowDormancyWrite = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var promotionAttempted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            var stop = Task.Run(() => coordinator.Execute(
                "party",
                () =>
                {
                    Assert.Equal(
                        PlaybackStopRegistryResolution.RetainParticipant,
                        registry.ResolvePlaybackStop(
                            "party",
                            "candidate",
                            "play-a",
                            out _));
                    stopResolved.TrySetResult(true);
                    allowDormancyWrite.Task.GetAwaiter().GetResult();
                    dormancies.MarkDormant(
                        "party",
                        "candidate",
                        "play-a",
                        now.AddSeconds(1));
                }));

            await stopResolved.Task;
            var promote = Task.Run(() =>
            {
                promotionAttempted.TrySetResult(true);
                var assignment = coordinator.TryAssignMaster(
                    "party",
                    "candidate",
                    replaceExisting: true,
                    retirePreviousAuthority: null);
                return assignment.Registered;
            });

            await promotionAttempted.Task;
            Assert.False(promote.IsCompleted);
            allowDormancyWrite.TrySetResult(true);
            await stop;
            var promoted = await promote;

            Assert.True(promoted);
            Assert.Equal("candidate", registry.GetMasterSession("party"));
            Assert.False(dormancies.IsDormant("party", "candidate"));
        }

        [Fact]
        public async Task ReplacementFirstRetiresOldAuthorityAndRejectsItsDelayedProgress()
        {
            var registry = new PartySessionRegistry();
            using var dormancies = new ParticipantDormancyTracker(
                Timeout.InfiniteTimeSpan);
            var coordinator = new MasterSessionLifecycleCoordinator(
                registry,
                dormancies);
            using var transitions = new PartyPlaybackTransitionCoordinator(
                CancellationToken.None);
            var now = new DateTime(2026, 8, 27, 3, 0, 0, DateTimeKind.Utc);
            registry.AddOrUpdate(
                "party",
                "master-a",
                Participant("master", "master-a", "play-a", now));
            registry.AddOrUpdate(
                "party",
                "master-b",
                Participant("master", "master-b", "play-b", now.AddSeconds(1)));
            Assert.True(registry.SetMasterSession("party", "master-a"));
            dormancies.MarkDormant(
                "party",
                "master-b",
                "play-b",
                now.AddSeconds(1));

            var oldProgressClassified = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var replacementCompleted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var authoritativePosition = 33L;
            var oldTransition = transitions.Begin("party");

            var delayedOldProgress = Task.Run(async () =>
            {
                Assert.True(registry.IsMasterSession("party", "master-a"));
                oldProgressClassified.TrySetResult(true);
                await replacementCompleted.Task;
                return coordinator.TryExecuteCurrentMasterPlayback(
                    "party",
                    "master-a",
                    "play-a",
                    () => authoritativePosition = 774L);
            });

            await oldProgressClassified.Task;
            var assignment = coordinator.TryAssignMaster(
                "party",
                "master-b",
                replaceExisting: true,
                previousMasterSessionId => transitions.Cancel("party"));
            replacementCompleted.TrySetResult(true);

            Assert.True(assignment.Registered);
            Assert.True(assignment.AuthorityChanged);
            Assert.Equal("master-a", assignment.PreviousMasterSessionId);
            Assert.True(oldTransition.Token.IsCancellationRequested);
            Assert.Equal("master-b", registry.GetMasterSession("party"));
            Assert.False(dormancies.IsDormant("party", "master-b"));
            Assert.False(await delayedOldProgress);
            Assert.Equal(33L, authoritativePosition);

            // A's delayed Stop is now a participant Stop. It must retain only A and
            // cannot disturb B's replacement authority.
            Assert.Equal(
                PlaybackStopRegistryResolution.RetainParticipant,
                registry.ResolvePlaybackStop(
                    "party",
                    "master-a",
                    "play-a",
                    out _));
            Assert.Equal("master-b", registry.GetMasterSession("party"));
        }

        [Fact]
        public void ReplacedPlaybackGenerationCannotCommitThroughAnOldMasterSnapshot()
        {
            var registry = new PartySessionRegistry();
            using var dormancies = new ParticipantDormancyTracker(
                Timeout.InfiniteTimeSpan);
            var coordinator = new MasterSessionLifecycleCoordinator(
                registry,
                dormancies);
            var now = new DateTime(2026, 8, 27, 3, 10, 0, DateTimeKind.Utc);
            registry.AddOrUpdate(
                "party",
                "master",
                Participant("master", "master", "play-a", now));
            Assert.True(registry.SetMasterSession("party", "master"));
            Assert.True(registry.IsMasterSession("party", "master"));

            coordinator.Execute(
                "party",
                () => registry.AddOrUpdate(
                    "party",
                    "master",
                    Participant("master", "master", "play-b", now.AddSeconds(1))));

            var authoritativePosition = 40L;
            var applied = coordinator.TryExecuteCurrentMasterPlayback(
                "party",
                "master",
                "play-a",
                () => authoritativePosition = 900L);

            Assert.False(applied);
            Assert.Equal(40L, authoritativePosition);
            Assert.True(coordinator.TryExecuteCurrentMasterPlayback(
                "party",
                "master",
                "play-b",
                () => authoritativePosition = 41L));
            Assert.Equal(41L, authoritativePosition);
        }

        [Fact]
        public void SamePartyLifecycleBoundaryIsReentrant()
        {
            var registry = new PartySessionRegistry();
            using var dormancies = new ParticipantDormancyTracker(
                Timeout.InfiniteTimeSpan);
            var coordinator = new MasterSessionLifecycleCoordinator(
                registry,
                dormancies);

            var result = 0;
            coordinator.Execute(
                "party",
                () => coordinator.Execute("party", () => result = 42));

            Assert.Equal(42, result);
        }

        private static PartyParticipant Participant(
            string userId,
            string sessionId,
            string playSessionId,
            DateTime nowUtc)
        {
            return new PartyParticipant
            {
                UserId = userId,
                UserName = userId,
                SessionId = sessionId,
                PlaySessionId = playSessionId,
                JoinedAt = nowUtc,
                LastActivityAt = nowUtc
            };
        }
    }
}
