using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class KeyedAsyncSerialQueueTests
    {
        [Fact]
        public async Task SecondOperationForTheSameKeyCannotOvertakeABlockedFirstOperation()
        {
            var queue = new KeyedAsyncSerialQueue<string>(capacityPerKey: 8);
            var firstStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var observedOrder = new List<string>();

            Assert.True(queue.TryEnqueue(
                "ios-session",
                async _ =>
                {
                    observedOrder.Add("first-start");
                    firstStarted.TrySetResult(true);
                    await releaseFirst.Task;
                    observedOrder.Add("first-end");
                },
                CancellationToken.None,
                out var firstCompletion));

            await firstStarted.Task;

            Assert.True(queue.TryEnqueue(
                "ios-session",
                _ =>
                {
                    observedOrder.Add("second");
                    return Task.CompletedTask;
                },
                CancellationToken.None,
                out var secondCompletion));

            Assert.False(secondCompletion.IsCompleted);
            Assert.Equal(new[] { "first-start" }, observedOrder);

            releaseFirst.TrySetResult(true);
            await Task.WhenAll(firstCompletion, secondCompletion);

            Assert.Equal(
                new[] { "first-start", "first-end", "second" },
                observedOrder);
        }

        [Fact]
        public async Task CapacityLimitRejectsExcessWorkWithoutRunningIt()
        {
            var queue = new KeyedAsyncSerialQueue<string>(capacityPerKey: 2);
            var firstStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var excessRan = false;

            Assert.True(queue.TryEnqueue(
                "ios-session",
                async _ =>
                {
                    firstStarted.TrySetResult(true);
                    await releaseFirst.Task;
                },
                CancellationToken.None,
                out var firstCompletion));
            await firstStarted.Task;

            Assert.True(queue.TryEnqueue(
                "ios-session",
                _ => Task.CompletedTask,
                CancellationToken.None,
                out var secondCompletion));
            Assert.False(queue.TryEnqueue(
                "ios-session",
                _ =>
                {
                    excessRan = true;
                    return Task.CompletedTask;
                },
                CancellationToken.None,
                out _));

            releaseFirst.TrySetResult(true);
            await Task.WhenAll(firstCompletion, secondCompletion);

            Assert.False(excessRan);
        }

        [Fact]
        public async Task DifferentKeysRunIndependentlyAndIdleKeysAreReleased()
        {
            var queue = new KeyedAsyncSerialQueue<string>(capacityPerKey: 2);
            var releaseFirstSession = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var secondSessionStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            Assert.True(queue.TryEnqueue(
                "ios-session",
                _ => releaseFirstSession.Task,
                CancellationToken.None,
                out var firstCompletion));
            Assert.True(queue.TryEnqueue(
                "web-session",
                _ =>
                {
                    secondSessionStarted.TrySetResult(true);
                    return Task.CompletedTask;
                },
                CancellationToken.None,
                out var secondCompletion));

            await secondSessionStarted.Task;
            await secondCompletion;
            Assert.False(firstCompletion.IsCompleted);

            releaseFirstSession.TrySetResult(true);
            await firstCompletion;

            Assert.Equal(0, queue.ActiveKeyCount);
        }

        [Fact]
        public async Task LatestCoalescedWorkReplacesQueuedOlderWork()
        {
            var queue = new KeyedAsyncSerialQueue<string>(capacityPerKey: 8);
            var firstStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var observed = new List<string>();

            Assert.True(queue.TryEnqueue(
                "ios-session",
                async _ =>
                {
                    observed.Add("running");
                    firstStarted.TrySetResult(true);
                    await releaseFirst.Task;
                },
                CancellationToken.None,
                out var firstCompletion));
            await firstStarted.Task;

            Assert.True(queue.TryEnqueueLatest(
                "ios-session",
                "seek",
                _ =>
                {
                    observed.Add("old-seek");
                    return Task.CompletedTask;
                },
                CancellationToken.None,
                out var oldSeekCompletion));
            Assert.True(queue.TryEnqueueLatest(
                "ios-session",
                "seek",
                _ =>
                {
                    observed.Add("new-seek");
                    return Task.CompletedTask;
                },
                CancellationToken.None,
                out var newSeekCompletion));

            releaseFirst.TrySetResult(true);
            await Task.WhenAll(firstCompletion, newSeekCompletion);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => oldSeekCompletion);
            Assert.Equal(new[] { "running", "new-seek" }, observed);
        }
    }
}
