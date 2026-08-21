using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class ParticipantResumeCoordinatorTests
    {
        [Fact]
        public async Task SlowSessionDoesNotBlockAnotherSessionsResumeAndSeek()
        {
            var coordinator = new ParticipantResumeCoordinator(capacityPerSession: 8);
            var releaseSlowResume = NewSignal();
            var slowResumeStarted = NewSignal();
            var fastOperations = new List<string>();

            var slowTask = coordinator.ResumeAsync(
                "slow-ios",
                async _ =>
                {
                    slowResumeStarted.TrySetResult(true);
                    await releaseSlowResume.Task;
                    return true;
                },
                () => 100L,
                (_, __) => Task.FromResult(true),
                CancellationToken.None);
            await slowResumeStarted.Task;

            var fastTask = coordinator.ResumeAsync(
                "fast-ios",
                _ =>
                {
                    fastOperations.Add("resume");
                    return Task.FromResult(true);
                },
                () =>
                {
                    fastOperations.Add("target");
                    return 200L;
                },
                (target, _) =>
                {
                    fastOperations.Add("seek:" + target);
                    return Task.FromResult(true);
                },
                CancellationToken.None);

            var firstCompletion = await Task.WhenAny(
                fastTask,
                Task.Delay(TimeSpan.FromSeconds(2)));

            Assert.Same(fastTask, firstCompletion);
            Assert.True(await fastTask);
            Assert.False(slowTask.IsCompleted);
            Assert.Equal(
                new[] { "resume", "target", "seek:200" },
                fastOperations);

            releaseSlowResume.TrySetResult(true);
            Assert.True(await slowTask);
        }

        [Fact]
        public async Task PipelinesForTheSameSessionRemainStrictlyOrdered()
        {
            var coordinator = new ParticipantResumeCoordinator(capacityPerSession: 8);
            var firstSeekStarted = NewSignal();
            var releaseFirstSeek = NewSignal();
            var observedOrder = new List<string>();

            var firstTask = coordinator.ResumeAsync(
                "ios",
                _ =>
                {
                    observedOrder.Add("first-resume");
                    return Task.FromResult(true);
                },
                () => 100L,
                async (_, __) =>
                {
                    observedOrder.Add("first-seek-start");
                    firstSeekStarted.TrySetResult(true);
                    await releaseFirstSeek.Task;
                    observedOrder.Add("first-seek-end");
                    return true;
                },
                CancellationToken.None);
            await firstSeekStarted.Task;

            var secondTask = coordinator.ResumeAsync(
                "ios",
                _ =>
                {
                    observedOrder.Add("second-resume");
                    return Task.FromResult(true);
                },
                () => 200L,
                (target, _) =>
                {
                    observedOrder.Add("second-seek:" + target);
                    return Task.FromResult(true);
                },
                CancellationToken.None);

            Assert.False(secondTask.IsCompleted);
            Assert.Equal(
                new[] { "first-resume", "first-seek-start" },
                observedOrder);

            releaseFirstSeek.TrySetResult(true);
            await Task.WhenAll(firstTask, secondTask);

            Assert.Equal(
                new[]
                {
                    "first-resume",
                    "first-seek-start",
                    "first-seek-end",
                    "second-resume",
                    "second-seek:200"
                },
                observedOrder);
        }

        [Fact]
        public async Task TargetIsProjectedAfterResumeCompletes()
        {
            var coordinator = new ParticipantResumeCoordinator(capacityPerSession: 8);
            var projectedPosition = 100L;
            var observedTarget = 0L;

            var completed = await coordinator.ResumeAsync(
                "ios",
                _ =>
                {
                    projectedPosition = 250L;
                    return Task.FromResult(true);
                },
                () => projectedPosition,
                (target, _) =>
                {
                    observedTarget = target;
                    return Task.FromResult(true);
                },
                CancellationToken.None);

            Assert.True(completed);
            Assert.Equal(250L, observedTarget);
        }

        [Fact]
        public async Task RejectedResumeSkipsTargetProjectionAndSeek()
        {
            var coordinator = new ParticipantResumeCoordinator(capacityPerSession: 8);
            var targetProjected = false;
            var seekSent = false;

            var completed = await coordinator.ResumeAsync(
                "dormant-ios",
                _ => Task.FromResult(false),
                () =>
                {
                    targetProjected = true;
                    return 100L;
                },
                (_, __) =>
                {
                    seekSent = true;
                    return Task.FromResult(true);
                },
                CancellationToken.None);

            Assert.False(completed);
            Assert.False(targetProjected);
            Assert.False(seekSent);
        }

        [Fact]
        public async Task CancelSessionStopsRunningResumeBeforeCompensationSeek()
        {
            var coordinator = new ParticipantResumeCoordinator(capacityPerSession: 8);
            var resumeStarted = NewSignal();
            var seekSent = false;

            var resumeTask = coordinator.ResumeAsync(
                "ios",
                async cancellationToken =>
                {
                    resumeStarted.TrySetResult(true);
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return true;
                },
                () => 100L,
                (_, __) =>
                {
                    seekSent = true;
                    return Task.FromResult(true);
                },
                CancellationToken.None);
            await resumeStarted.Task;

            coordinator.CancelSession("ios");

            Assert.False(await resumeTask);
            Assert.False(seekSent);
        }

        [Fact]
        public async Task CancelSessionStopsRunningAndQueuedPipelines()
        {
            var coordinator = new ParticipantResumeCoordinator(capacityPerSession: 8);
            var firstResumeStarted = NewSignal();
            var firstSeekSent = false;
            var secondResumeSent = false;

            var firstTask = coordinator.ResumeAsync(
                "ios",
                async cancellationToken =>
                {
                    firstResumeStarted.TrySetResult(true);
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return true;
                },
                () => 100L,
                (_, __) =>
                {
                    firstSeekSent = true;
                    return Task.FromResult(true);
                },
                CancellationToken.None);
            await firstResumeStarted.Task;

            var secondTask = coordinator.ResumeAsync(
                "ios",
                _ =>
                {
                    secondResumeSent = true;
                    return Task.FromResult(true);
                },
                () => 200L,
                (_, __) => Task.FromResult(true),
                CancellationToken.None);

            coordinator.CancelSession("ios");

            Assert.False(await firstTask);
            Assert.False(await secondTask);
            Assert.False(firstSeekSent);
            Assert.False(secondResumeSent);
        }

        [Fact]
        public async Task CancelSessionDuringSeekConfirmationPreventsSecondSeek()
        {
            var coordinator = new ParticipantResumeCoordinator(capacityPerSession: 8);
            var confirmationWaitStarted = NewSignal();
            var seekCount = 0;
            var seekRetrier = new ConfirmedPlaybackSeekRetrier(
                maxAttempts: 2,
                confirmationTimeout: TimeSpan.FromSeconds(6),
                delayAsync: async (_, cancellationToken) =>
                {
                    confirmationWaitStarted.TrySetResult(true);
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                });

            var resumeTask = coordinator.ResumeAsync(
                "ios",
                _ => Task.FromResult(true),
                () => 100L,
                (target, cancellationToken) => seekRetrier.SendAsync(
                    "ios",
                    target,
                    getRetryTargetPositionTicks: () => 200L,
                    sendSeek: (_, __) =>
                    {
                        seekCount++;
                        return Task.FromResult(true);
                    },
                    cancellationToken),
                CancellationToken.None);
            await confirmationWaitStarted.Task;

            coordinator.CancelSession("ios");

            Assert.False(await resumeTask);
            Assert.Equal(1, seekCount);
        }

        private static TaskCompletionSource<bool> NewSignal()
        {
            return new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
