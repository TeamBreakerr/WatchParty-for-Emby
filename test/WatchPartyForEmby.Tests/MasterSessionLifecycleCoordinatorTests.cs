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
            var coordinator = new MasterSessionLifecycleCoordinator();
            var registry = new PartySessionRegistry();
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
                return coordinator.Execute(
                    "party",
                    () =>
                    {
                        Assert.True(cleanupCompleted);
                        return registry.SetMasterSession("party", "master-b");
                    });
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
            var coordinator = new MasterSessionLifecycleCoordinator();
            var registry = new PartySessionRegistry();
            using var dormancies = new ParticipantDormancyTracker(
                Timeout.InfiniteTimeSpan);
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
                return coordinator.Execute(
                    "party",
                    () =>
                    {
                        var registered = registry.SetMasterSession(
                            "party",
                            "candidate");
                        if (registered)
                        {
                            dormancies.Cancel("party", "candidate");
                        }
                        return registered;
                    });
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
        public void SamePartyLifecycleBoundaryIsReentrant()
        {
            var coordinator = new MasterSessionLifecycleCoordinator();

            var result = coordinator.Execute(
                "party",
                () => coordinator.Execute("party", () => 42));

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
