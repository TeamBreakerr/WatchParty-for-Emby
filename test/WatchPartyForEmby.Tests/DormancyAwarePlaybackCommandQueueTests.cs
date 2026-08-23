using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class DormancyAwarePlaybackCommandQueueTests
    {
        [Fact]
        public async Task DormantParticipantPlaybackCommandIsNotExecuted()
        {
            using (var dormancies = new ParticipantDormancyTracker(Timeout.InfiniteTimeSpan))
            {
                var queue = new DormancyAwarePlaybackCommandQueue(dormancies, 8);
                dormancies.MarkDormant(
                    "party",
                    "old-ios",
                    "playback-1",
                    DateTime.UtcNow);
                var invocationCount = 0;

                var sent = await queue.EnqueueAsync(
                    "party",
                    "old-ios",
                    ParticipantRoomCommand.PlayNow,
                    ParticipantCommandQueueMode.Ordered,
                    _ =>
                    {
                        invocationCount++;
                        return Task.CompletedTask;
                    },
                    CancellationToken.None);

                Assert.False(sent);
                Assert.Equal(0, invocationCount);
            }
        }

        [Fact]
        public async Task DormantParticipantCanStillReceiveStopForTeardown()
        {
            using (var dormancies = new ParticipantDormancyTracker(Timeout.InfiniteTimeSpan))
            {
                var queue = new DormancyAwarePlaybackCommandQueue(dormancies, 8);
                dormancies.MarkDormant(
                    "party",
                    "old-ios",
                    "playback-1",
                    DateTime.UtcNow);
                var invocationCount = 0;

                var sent = await queue.EnqueueAsync(
                    "party",
                    "old-ios",
                    ParticipantRoomCommand.Stop,
                    ParticipantCommandQueueMode.Ordered,
                    _ =>
                    {
                        invocationCount++;
                        return Task.CompletedTask;
                    },
                    CancellationToken.None);

                Assert.True(sent);
                Assert.Equal(1, invocationCount);
            }
        }

        [Fact]
        public async Task ExplicitManualPlayCanReachDormantSessionWithoutReactivatingIt()
        {
            using (var dormancies = new ParticipantDormancyTracker(Timeout.InfiniteTimeSpan))
            {
                var queue = new DormancyAwarePlaybackCommandQueue(dormancies, 8);
                dormancies.MarkDormant(
                    "party",
                    "old-ios",
                    "playback-1",
                    DateTime.UtcNow);
                var invocationCount = 0;

                var sent = await queue.EnqueueExplicitPlayNowAsync(
                    "party",
                    "old-ios",
                    _ =>
                    {
                        invocationCount++;
                        return Task.CompletedTask;
                    },
                    CancellationToken.None);

                Assert.True(sent);
                Assert.Equal(1, invocationCount);
                Assert.True(dormancies.IsDormant("party", "old-ios"));
            }
        }

        [Fact]
        public async Task CommandQueuedBeforeStopIsDroppedWhenDormancyBegins()
        {
            using (var dormancies = new ParticipantDormancyTracker(Timeout.InfiniteTimeSpan))
            {
                var queue = new DormancyAwarePlaybackCommandQueue(dormancies, 8);
                var firstStarted = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var releaseFirst = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var queuedInvocationCount = 0;

                var runningStop = queue.EnqueueAsync(
                    "party",
                    "old-ios",
                    ParticipantRoomCommand.Stop,
                    ParticipantCommandQueueMode.Ordered,
                    async _ =>
                    {
                        firstStarted.TrySetResult(true);
                        await releaseFirst.Task.ConfigureAwait(false);
                    },
                    CancellationToken.None);
                await firstStarted.Task;

                var queuedPlay = queue.EnqueueAsync(
                    "party",
                    "old-ios",
                    ParticipantRoomCommand.PlayNow,
                    ParticipantCommandQueueMode.Ordered,
                    _ =>
                    {
                        queuedInvocationCount++;
                        return Task.CompletedTask;
                    },
                    CancellationToken.None);

                dormancies.MarkDormant(
                    "party",
                    "old-ios",
                    "playback-1",
                    DateTime.UtcNow);
                releaseFirst.TrySetResult(true);

                Assert.True(await runningStop);
                Assert.False(await queuedPlay);
                Assert.Equal(0, queuedInvocationCount);
            }
        }

        [Fact]
        public async Task BlockedSessionDoesNotDelayAHealthySession()
        {
            using (var dormancies = new ParticipantDormancyTracker(Timeout.InfiniteTimeSpan))
            {
                var queue = new DormancyAwarePlaybackCommandQueue(dormancies, 8);
                var blockedStarted = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var releaseBlocked = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                var blocked = queue.EnqueueAsync(
                    "party",
                    "disconnected-ios",
                    ParticipantRoomCommand.Pause,
                    ParticipantCommandQueueMode.Ordered,
                    async _ =>
                    {
                        blockedStarted.TrySetResult(true);
                        await releaseBlocked.Task.ConfigureAwait(false);
                    },
                    CancellationToken.None);
                await blockedStarted.Task;

                var healthy = queue.EnqueueAsync(
                    "party",
                    "healthy-ios",
                    ParticipantRoomCommand.Pause,
                    ParticipantCommandQueueMode.Ordered,
                    _ => Task.CompletedTask,
                    CancellationToken.None);

                Assert.True(await healthy);
                Assert.False(blocked.IsCompleted);
                releaseBlocked.TrySetResult(true);
                Assert.True(await blocked);
            }
        }
    }
}
