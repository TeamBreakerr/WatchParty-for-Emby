using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class ConfirmedParticipantPauseSynchronizerTests
    {
        [Fact]
        public async Task PauseAlwaysCompletesBeforeTheExactSeekIsSent()
        {
            var operations = new List<string>();
            var synchronizer = new ConfirmedParticipantPauseSynchronizer(
                maxAttempts: 1,
                confirmationTimeout: TimeSpan.FromSeconds(1),
                delayAsync: (_, __) => Task.CompletedTask);

            var completed = await synchronizer.SendAsync(
                "ios-session",
                targetPositionTicks: 123L,
                sendPause: _ =>
                {
                    operations.Add("pause");
                    return Task.FromResult(true);
                },
                sendSeek: (target, _) =>
                {
                    operations.Add("seek:" + target);
                    synchronizer.Confirm("ios-session");
                    return Task.FromResult(true);
                },
                CancellationToken.None);

            Assert.True(completed);
            Assert.Equal(new[] { "pause", "seek:123" }, operations);
        }

        [Fact]
        public async Task MissingFinalPausedPositionRetriesPauseAndSeekAsOneUnit()
        {
            var operations = new List<string>();
            var waits = 0;
            var synchronizer = new ConfirmedParticipantPauseSynchronizer(
                maxAttempts: 2,
                confirmationTimeout: TimeSpan.FromSeconds(1),
                delayAsync: (_, __) =>
                {
                    waits++;
                    return Task.CompletedTask;
                });

            var completed = await synchronizer.SendAsync(
                "ios-session",
                targetPositionTicks: 456L,
                sendPause: _ =>
                {
                    operations.Add("pause");
                    return Task.FromResult(true);
                },
                sendSeek: (target, _) =>
                {
                    operations.Add("seek:" + target);
                    if (operations.Count == 4)
                    {
                        synchronizer.Confirm("ios-session");
                    }
                    return Task.FromResult(true);
                },
                CancellationToken.None);

            Assert.True(completed);
            Assert.Equal(1, waits);
            Assert.Equal(
                new[] { "pause", "seek:456", "pause", "seek:456" },
                operations);
        }

        [Fact]
        public async Task RejectedPauseNeverSendsASeek()
        {
            var seekSent = false;
            var synchronizer = new ConfirmedParticipantPauseSynchronizer(
                maxAttempts: 2,
                confirmationTimeout: TimeSpan.FromSeconds(1),
                delayAsync: (_, __) => Task.CompletedTask);

            var completed = await synchronizer.SendAsync(
                "ios-session",
                targetPositionTicks: 789L,
                sendPause: _ => Task.FromResult(false),
                sendSeek: (_, __) =>
                {
                    seekSent = true;
                    return Task.FromResult(true);
                },
                CancellationToken.None);

            Assert.False(completed);
            Assert.False(seekSent);
        }
    }
}
