using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class PluginConfigurationTests
    {
        [Fact]
        public void ExternalDashboardIsDisabledByDefault()
        {
            var configuration = new PluginConfiguration();

            Assert.False(configuration.EnableExternalWebServer);
        }

        [Fact]
        public void ExternalDashboardDefaultsToLoopbackOnly()
        {
            var configuration = new PluginConfiguration();

            Assert.Equal("127.0.0.1", configuration.ListenAddress);
        }

        [Fact]
        public void NewRoomsUseTheTwoSecondSeekTolerance()
        {
            Assert.Equal(2, new WatchPartyItem().SyncToleranceSeconds);
        }

        [Fact]
        public void DirectItemUpgradePreservesLegacyRoomsAndIsIdempotent()
        {
            var configuration = new PluginConfiguration();
            var room = new WatchPartyItem { ItemId = "1234" };
            configuration.WatchParties.Add(room);

            var preservedRoomCount = PluginConfigurationMigration.UpgradeLegacyRooms(configuration);

            Assert.Equal(1, preservedRoomCount);
            Assert.Single(configuration.WatchParties);
            Assert.Same(room, configuration.WatchParties[0]);
            Assert.Equal(PluginConfigurationMigration.DirectItemBindingVersion, configuration.ConfigurationVersion);
            Assert.Equal(0, PluginConfigurationMigration.UpgradeLegacyRooms(configuration));
        }
    }
}
