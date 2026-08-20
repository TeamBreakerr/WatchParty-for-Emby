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
    }
}
