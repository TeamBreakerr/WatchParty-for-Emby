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
        public void DirectItemUpgradeDeletesLegacyRoomsAndIsIdempotent()
        {
            var configuration = new PluginConfiguration();
            configuration.WatchParties.Add(new WatchPartyItem { ItemId = "1234" });

            var removedRoomCount = PluginConfigurationMigration.ResetLegacyRooms(configuration);

            Assert.Equal(1, removedRoomCount);
            Assert.Empty(configuration.WatchParties);
            Assert.Equal(PluginConfigurationMigration.DirectItemBindingVersion, configuration.ConfigurationVersion);
            Assert.Equal(0, PluginConfigurationMigration.ResetLegacyRooms(configuration));
        }
    }
}
