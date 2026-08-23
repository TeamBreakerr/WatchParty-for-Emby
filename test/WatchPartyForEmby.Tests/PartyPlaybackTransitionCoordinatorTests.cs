using System.Threading;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class PartyPlaybackTransitionCoordinatorTests
    {
        [Fact]
        public void NewPlaybackStateCancelsThePreviousGenerationOnly()
        {
            using (var coordinator = new PartyPlaybackTransitionCoordinator(
                CancellationToken.None))
            {
                var pause = coordinator.Begin("party");
                var resume = coordinator.Begin("party");

                Assert.True(pause.Token.IsCancellationRequested);
                Assert.False(resume.Token.IsCancellationRequested);

                coordinator.Complete(pause);
                Assert.False(resume.Token.IsCancellationRequested);
                coordinator.Complete(resume);
            }
        }

        [Fact]
        public void ExplicitCancellationStopsTheCurrentGeneration()
        {
            using (var coordinator = new PartyPlaybackTransitionCoordinator(
                CancellationToken.None))
            {
                var pause = coordinator.Begin("party");

                coordinator.Cancel("party");

                Assert.True(pause.Token.IsCancellationRequested);
                coordinator.Complete(pause);
            }
        }
    }
}
