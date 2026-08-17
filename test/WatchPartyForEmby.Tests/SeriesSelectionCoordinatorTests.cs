using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class SeriesSelectionCoordinatorTests
    {
        [Fact]
        public async Task RapidReplacementRunsOnlyTheLatestEpisodeSelection()
        {
            var coordinator = new SeriesSelectionCoordinator();
            var sentEpisodes = new List<string>();
            var selectionB = coordinator.Register("party", "episode-b");
            var selectionC = coordinator.Register("party", "episode-c");

            var runB = coordinator.RunIfLatestAsync(
                selectionB,
                TimeSpan.Zero,
                _ =>
                {
                    sentEpisodes.Add("episode-b");
                    return Task.CompletedTask;
                },
                CancellationToken.None);

            var runC = coordinator.RunIfLatestAsync(
                selectionC,
                TimeSpan.Zero,
                _ =>
                {
                    sentEpisodes.Add("episode-c");
                    return Task.CompletedTask;
                },
                CancellationToken.None);

            var results = await Task.WhenAll(runB, runC);

            Assert.False(results[0]);
            Assert.True(results[1]);
            Assert.Equal(new[] { "episode-c" }, sentEpisodes);
        }

        [Fact]
        public async Task ReplacementEndsTheOlderQuietPeriodWithoutRunningItsCommand()
        {
            var coordinator = new SeriesSelectionCoordinator();
            var selectionB = coordinator.Register("party", "episode-b");
            var commandBWasSent = false;
            var runB = coordinator.RunIfLatestAsync(
                selectionB,
                TimeSpan.FromSeconds(5),
                _ =>
                {
                    commandBWasSent = true;
                    return Task.CompletedTask;
                },
                CancellationToken.None);

            coordinator.Register("party", "episode-c");

            var completed = await Task.WhenAny(runB, Task.Delay(TimeSpan.FromSeconds(1)));
            Assert.Same(runB, completed);
            Assert.False(await runB);
            Assert.False(commandBWasSent);
        }

        [Fact]
        public async Task ReplacementDoesNotCancelACommandBatchThatAlreadyStarted()
        {
            var coordinator = new SeriesSelectionCoordinator();
            var selectionB = coordinator.Register("party", "episode-b");
            var batchStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var finishBatch = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var sentCommands = new List<string>();
            var runB = coordinator.RunIfLatestAsync(
                selectionB,
                TimeSpan.Zero,
                async token =>
                {
                    sentCommands.Add("episode-b/device-1");
                    batchStarted.TrySetResult(true);
                    await finishBatch.Task.WaitAsync(token);
                    sentCommands.Add("episode-b/device-2");
                },
                CancellationToken.None);

            await batchStarted.Task;
            coordinator.Register("party", "episode-c");

            var completedEarly = await Task.WhenAny(
                runB,
                Task.Delay(TimeSpan.FromMilliseconds(250)));
            Assert.NotSame(runB, completedEarly);

            finishBatch.TrySetResult(true);
            Assert.True(await runB);
            Assert.Equal(
                new[] { "episode-b/device-1", "episode-b/device-2" },
                sentCommands);
        }
    }
}
