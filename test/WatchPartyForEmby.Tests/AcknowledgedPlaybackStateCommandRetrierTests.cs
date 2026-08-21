using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class AcknowledgedPlaybackStateCommandRetrierTests
    {
        [Fact]
        public async Task AcknowledgementDuringRetryWaitPreventsDuplicateCommand()
        {
            var waitStarted = NewSignal();
            var releaseWait = NewSignal();
            var acknowledgementPending = true;
            var sendCount = 0;
            var retrier = new AcknowledgedPlaybackStateCommandRetrier(
                maxAttempts: 3,
                retryDelay: TimeSpan.FromMilliseconds(350),
                delayAsync: async (_, cancellationToken) =>
                {
                    waitStarted.TrySetResult(true);
                    await releaseWait.Task.WaitAsync(cancellationToken);
                });

            var sendTask = retrier.SendAsync(
                _ =>
                {
                    sendCount++;
                    return Task.FromResult(true);
                },
                () => acknowledgementPending,
                onAttemptFailed: null,
                onAcknowledgementMissing: null,
                CancellationToken.None);
            await waitStarted.Task;

            acknowledgementPending = false;
            releaseWait.TrySetResult(true);

            Assert.True(await sendTask);
            Assert.Equal(1, sendCount);
        }

        [Fact]
        public async Task MissingAcknowledgementKeepsTheExistingBoundedRetries()
        {
            var sendCount = 0;
            var retrier = new AcknowledgedPlaybackStateCommandRetrier(
                maxAttempts: 3,
                retryDelay: TimeSpan.FromMilliseconds(350),
                delayAsync: (_, __) => Task.CompletedTask);

            var sent = await retrier.SendAsync(
                _ =>
                {
                    sendCount++;
                    return Task.FromResult(true);
                },
                acknowledgementPending: () => true,
                onAttemptFailed: null,
                onAcknowledgementMissing: null,
                CancellationToken.None);

            Assert.True(sent);
            Assert.Equal(3, sendCount);
        }

        [Fact]
        public async Task AcknowledgementAfterAmbiguousSendFailurePreventsDuplicateCommand()
        {
            var waitStarted = NewSignal();
            var releaseWait = NewSignal();
            var acknowledgementPending = true;
            var sendCount = 0;
            var retrier = new AcknowledgedPlaybackStateCommandRetrier(
                maxAttempts: 3,
                retryDelay: TimeSpan.FromMilliseconds(350),
                delayAsync: async (_, cancellationToken) =>
                {
                    waitStarted.TrySetResult(true);
                    await releaseWait.Task.WaitAsync(cancellationToken);
                });

            var sendTask = retrier.SendAsync(
                _ =>
                {
                    sendCount++;
                    throw new InvalidOperationException("ambiguous transport failure");
                },
                () => acknowledgementPending,
                onAttemptFailed: null,
                onAcknowledgementMissing: null,
                CancellationToken.None);
            await waitStarted.Task;

            acknowledgementPending = false;
            releaseWait.TrySetResult(true);

            Assert.True(await sendTask);
            Assert.Equal(1, sendCount);
        }

        private static TaskCompletionSource<bool> NewSignal()
        {
            return new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
