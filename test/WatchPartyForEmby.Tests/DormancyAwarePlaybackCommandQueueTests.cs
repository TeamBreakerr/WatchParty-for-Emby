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
        public async Task ExpiredGenerationAuthorizationDropsAQueuedExplicitPlayNow()
        {
            using (var dormancies = new ParticipantDormancyTracker(Timeout.InfiniteTimeSpan))
            {
                var queue = new DormancyAwarePlaybackCommandQueue(dormancies, 8);
                dormancies.MarkDormant(
                    "party",
                    "ios",
                    "old-playback",
                    DateTime.UtcNow);
                var firstStarted = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var releaseFirst = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var currentPlaySessionId = "old-playback";
                var invocationCount = 0;

                var blocker = queue.EnqueueExplicitPlayNowAsync(
                    "party",
                    "ios",
                    async _ =>
                    {
                        firstStarted.TrySetResult(true);
                        await releaseFirst.Task.ConfigureAwait(false);
                    },
                    CancellationToken.None);
                await firstStarted.Task;

                var queued = queue.EnqueueExplicitPlayNowAsync(
                    "party",
                    "ios",
                    _ =>
                    {
                        invocationCount++;
                        return Task.CompletedTask;
                    },
                    CancellationToken.None,
                    authorizationStillValid: () =>
                        currentPlaySessionId == "old-playback");

                currentPlaySessionId = "replacement-playback";
                releaseFirst.TrySetResult(true);

                Assert.True(await blocker);
                Assert.False(await queued);
                Assert.Equal(0, invocationCount);
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

        [Fact]
        public async Task ProviderBurstsAreSpacedAcrossParticipantsOfOneParty()
        {
            using (var dormancies = new ParticipantDormancyTracker(Timeout.InfiniteTimeSpan))
            {
                var delays = new System.Collections.Generic.List<TimeSpan>();
                var now = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);
                var pacer = new ProviderBurstPacer(
                    2,
                    TimeSpan.FromMilliseconds(400),
                    (delay, _) =>
                    {
                        delays.Add(delay);
                        return Task.CompletedTask;
                    },
                    () => now);
                var queue = new DormancyAwarePlaybackCommandQueue(dormancies, 8, pacer);

                for (var participant = 0; participant < 3; participant++)
                {
                    var sent = await queue.EnqueueAsync(
                        "party",
                        "session-" + participant,
                        ParticipantRoomCommand.Seek,
                        ParticipantCommandQueueMode.Ordered,
                        _ => Task.CompletedTask,
                        CancellationToken.None);
                    Assert.True(sent);
                }

                // Two participants seek together; only the third waits.
                Assert.Equal(new[] { TimeSpan.FromMilliseconds(400) }, delays);
            }
        }

        [Fact]
        public async Task PausingIsNeverDelayedByProviderPacing()
        {
            using (var dormancies = new ParticipantDormancyTracker(Timeout.InfiniteTimeSpan))
            {
                var paced = 0;
                var now = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);
                var pacer = new ProviderBurstPacer(
                    1,
                    TimeSpan.FromMilliseconds(400),
                    (_, __) =>
                    {
                        paced++;
                        return Task.CompletedTask;
                    },
                    () => now);
                var queue = new DormancyAwarePlaybackCommandQueue(dormancies, 8, pacer);

                for (var participant = 0; participant < 4; participant++)
                {
                    await queue.EnqueueAsync(
                        "party",
                        "session-" + participant,
                        ParticipantRoomCommand.Pause,
                        ParticipantCommandQueueMode.Ordered,
                        _ => Task.CompletedTask,
                        CancellationToken.None);
                }

                Assert.Equal(0, paced);
            }
        }

        [Fact]
        public async Task AParticipantThatGoesDormantWhileWaitingIsNotCommanded()
        {
            using (var dormancies = new ParticipantDormancyTracker(Timeout.InfiniteTimeSpan))
            {
                var now = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);
                var pacer = new ProviderBurstPacer(
                    1,
                    TimeSpan.FromMilliseconds(400),
                    (_, __) =>
                    {
                        // Stop arrives while this command is spacing itself out.
                        dormancies.MarkDormant("party", "late", "playback-1", now);
                        return Task.CompletedTask;
                    },
                    () => now);
                var queue = new DormancyAwarePlaybackCommandQueue(dormancies, 8, pacer);
                var invoked = 0;

                await queue.EnqueueAsync(
                    "party",
                    "first",
                    ParticipantRoomCommand.Seek,
                    ParticipantCommandQueueMode.Ordered,
                    _ => Task.CompletedTask,
                    CancellationToken.None);
                var sent = await queue.EnqueueAsync(
                    "party",
                    "late",
                    ParticipantRoomCommand.Seek,
                    ParticipantCommandQueueMode.Ordered,
                    _ =>
                    {
                        invoked++;
                        return Task.CompletedTask;
                    },
                    CancellationToken.None);

                Assert.False(sent);
                Assert.Equal(0, invoked);
            }
        }
    }
}
