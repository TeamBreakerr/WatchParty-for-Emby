using System.Threading.Tasks;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class PartyReadyRegistryTests
    {
        [Fact]
        public void SetReadyTracksEachUserAndCanRemoveThem()
        {
            var registry = new PartyReadyRegistry();

            registry.SetReady("party", "user-1", true);
            registry.SetReady("party", "user-2", true);

            Assert.True(registry.IsReady("party", "user-1"));
            Assert.True(registry.IsReady("party", "user-2"));
            Assert.Equal(2, registry.Count("party"));

            registry.SetReady("party", "user-1", false);

            Assert.False(registry.IsReady("party", "user-1"));
            Assert.True(registry.IsReady("party", "user-2"));
            Assert.Equal(1, registry.Count("party"));
        }

        [Fact]
        public void ReadyUserSnapshotCannotObserveOrChangeLaterRegistryUpdates()
        {
            var registry = new PartyReadyRegistry();
            registry.SetReady("party", "user-1", true);

            var snapshot = registry.GetReadyUsersSnapshot("party");

            registry.SetReady("party", "user-1", false);
            registry.SetReady("party", "user-2", true);

            Assert.Single(snapshot);
            Assert.Contains("user-1", snapshot);
            Assert.DoesNotContain("user-2", snapshot);
            Assert.False(registry.IsReady("party", "user-1"));
            Assert.True(registry.IsReady("party", "user-2"));
        }

        [Fact]
        public void RemoveUserOnlyChangesTheRequestedParty()
        {
            var registry = new PartyReadyRegistry();
            registry.SetReady("party-1", "user-1", true);
            registry.SetReady("party-2", "user-1", true);

            Assert.True(registry.RemoveUser("party-1", "user-1"));

            Assert.False(registry.IsReady("party-1", "user-1"));
            Assert.True(registry.IsReady("party-2", "user-1"));
            Assert.False(registry.RemoveUser("party-1", "user-1"));
        }

        [Fact]
        public void ClearPartyDoesNotClearOtherParties()
        {
            var registry = new PartyReadyRegistry();
            registry.SetReady("party-1", "user-1", true);
            registry.SetReady("party-2", "user-2", true);

            registry.ClearParty("party-1");

            Assert.Equal(0, registry.Count("party-1"));
            Assert.False(registry.IsReady("party-1", "user-1"));
            Assert.Equal(1, registry.Count("party-2"));
            Assert.True(registry.IsReady("party-2", "user-2"));
        }

        [Fact]
        public void ConcurrentUpdatesDoNotLoseReadyUsers()
        {
            var registry = new PartyReadyRegistry();

            Parallel.For(0, 500, index =>
                registry.SetReady("party", "user-" + index, true));

            Assert.Equal(500, registry.Count("party"));
            Assert.Equal(500, registry.GetReadyUsersSnapshot("party").Count);

            Parallel.For(0, 500, index =>
                registry.RemoveUser("party", "user-" + index));

            Assert.Equal(0, registry.Count("party"));
        }
    }
}
