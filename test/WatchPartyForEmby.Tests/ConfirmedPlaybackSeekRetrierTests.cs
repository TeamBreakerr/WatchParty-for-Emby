using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class ConfirmedPlaybackSeekRetrierTests
    {
        [Fact]
        public async Task UnconfirmedSeekRetriesOnceWithFreshTargetAndStopsAfterConfirmation()
        {
            var firstConfirmationWaitStarted = NewSignal();
            var releaseFirstConfirmationWait = NewSignal();
            var targets = new List<long>();
            var retrier = new ConfirmedPlaybackSeekRetrier(
                maxAttempts: 2,
                confirmationTimeout: TimeSpan.FromSeconds(1),
                delayAsync: async (_, cancellationToken) =>
                {
                    firstConfirmationWaitStarted.TrySetResult(true);
                    await releaseFirstConfirmationWait.Task.WaitAsync(cancellationToken);
                });

            var sendTask = retrier.SendAsync(
                "ios-session",
                initialTargetPositionTicks: 100L,
                getRetryTargetPositionTicks: () => 200L,
                sendSeek: (target, _) =>
                {
                    targets.Add(target);
                    if (targets.Count == 2)
                    {
                        retrier.Confirm("ios-session");
                    }
                    return Task.FromResult(true);
                },
                CancellationToken.None);
            await firstConfirmationWaitStarted.Task;

            releaseFirstConfirmationWait.TrySetResult(true);

            Assert.True(await sendTask);
            Assert.Equal(new long[] { 100L, 200L }, targets);
        }

        [Fact]
        public async Task NeverConfirmedSeekStopsAfterTheBoundedRetry()
        {
            var firstWaitStarted = NewSignal();
            var secondWaitStarted = NewSignal();
            var releaseFirstWait = NewSignal();
            var releaseSecondWait = NewSignal();
            var waitCount = 0;
            var targets = new List<long>();
            var retrier = new ConfirmedPlaybackSeekRetrier(
                maxAttempts: 2,
                confirmationTimeout: TimeSpan.FromSeconds(1),
                delayAsync: async (_, cancellationToken) =>
                {
                    var currentWait = Interlocked.Increment(ref waitCount);
                    if (currentWait == 1)
                    {
                        firstWaitStarted.TrySetResult(true);
                        await releaseFirstWait.Task.WaitAsync(cancellationToken);
                    }
                    else
                    {
                        secondWaitStarted.TrySetResult(true);
                        await releaseSecondWait.Task.WaitAsync(cancellationToken);
                    }
                });

            var sendTask = retrier.SendAsync(
                "ios-session",
                initialTargetPositionTicks: 100L,
                getRetryTargetPositionTicks: () => 200L,
                sendSeek: (target, _) =>
                {
                    targets.Add(target);
                    return Task.FromResult(true);
                },
                CancellationToken.None);
            await firstWaitStarted.Task;
            releaseFirstWait.TrySetResult(true);
            await secondWaitStarted.Task;
            releaseSecondWait.TrySetResult(true);

            Assert.False(await sendTask);
            Assert.Equal(new long[] { 100L, 200L }, targets);
        }

        [Fact]
        public async Task CancellationRemovesTheSessionRegistration()
        {
            var waitStarted = NewSignal();
            using var cancellation = new CancellationTokenSource();
            var retrier = new ConfirmedPlaybackSeekRetrier(
                maxAttempts: 2,
                confirmationTimeout: TimeSpan.FromSeconds(1),
                delayAsync: async (_, cancellationToken) =>
                {
                    waitStarted.TrySetResult(true);
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                });

            var canceledSend = retrier.SendAsync(
                "ios-session",
                initialTargetPositionTicks: 100L,
                getRetryTargetPositionTicks: () => 200L,
                sendSeek: (_, __) => Task.FromResult(true),
                cancellation.Token);
            await waitStarted.Task;

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledSend);

            var replacementSend = retrier.SendAsync(
                "ios-session",
                initialTargetPositionTicks: 300L,
                getRetryTargetPositionTicks: () => 400L,
                sendSeek: (_, __) =>
                {
                    retrier.Confirm("ios-session");
                    return Task.FromResult(true);
                },
                CancellationToken.None);

            Assert.True(await replacementSend);
        }

        private static TaskCompletionSource<bool> NewSignal()
        {
            return new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
