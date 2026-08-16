using System.Threading.Tasks;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class WaitingRoomStartCoordinatorTests
    {
        [Fact]
        public async Task ConcurrentManualAndAutomaticRequestsShareOneRuntimeStart()
        {
            var coordinator = new WaitingRoomStartCoordinator();
            var runtimeEntered = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var allowRuntimeToFinish = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var runtimeCallCount = 0;

            using (coordinator.Register(async partyId =>
            {
                Assert.Equal("party-one", partyId);
                runtimeCallCount++;
                runtimeEntered.SetResult(true);
                await allowRuntimeToFinish.Task;
                return true;
            }))
            {
                var manualStart = coordinator.StartAsync("party-one");
                await runtimeEntered.Task;
                var automaticStart = coordinator.StartAsync("party-one");

                Assert.Equal(1, runtimeCallCount);

                allowRuntimeToFinish.SetResult(true);

                Assert.True(await manualStart);
                Assert.True(await automaticStart);
                Assert.Equal(1, runtimeCallCount);
            }
        }
    }
}
