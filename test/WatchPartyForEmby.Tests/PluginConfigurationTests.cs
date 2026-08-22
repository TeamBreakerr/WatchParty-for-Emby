using System.IO;
using System.Xml.Serialization;
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
        public void DefaultMasterIsUnspecifiedByDefault()
        {
            var configuration = new PluginConfiguration();

            Assert.Equal(string.Empty, configuration.DefaultMasterUserId);
        }

        [Fact]
        public void DefaultMasterRoundTripsThroughXmlConfiguration()
        {
            var serializer = new XmlSerializer(typeof(PluginConfiguration));
            var source = new PluginConfiguration { DefaultMasterUserId = "team-id" };

            PluginConfiguration restored;
            using (var writer = new StringWriter())
            {
                serializer.Serialize(writer, source);
                using (var reader = new StringReader(writer.ToString()))
                {
                    restored = (PluginConfiguration)serializer.Deserialize(reader);
                }
            }

            Assert.Equal("team-id", restored.DefaultMasterUserId);
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

        [Fact]
        public void LegacyPauseControlElementIsIgnoredWithoutLosingRoomConfiguration()
        {
            var serializer = new XmlSerializer(typeof(PluginConfiguration));
            var source = new PluginConfiguration();
            source.WatchParties.Add(new WatchPartyItem
            {
                Id = "legacy-room",
                ItemName = "Legacy room",
                MasterUserId = "master-user",
                SyncToleranceSeconds = 7,
                MaxParticipants = 12
            });

            string xml;
            using (var writer = new StringWriter())
            {
                serializer.Serialize(writer, source);
                xml = writer.ToString();
            }

            xml = xml.Replace(
                "<SyncToleranceSeconds>7</SyncToleranceSeconds>",
                "<PauseControl>Anyone</PauseControl><SyncToleranceSeconds>7</SyncToleranceSeconds>");
            Assert.Contains("<PauseControl>Anyone</PauseControl>", xml);

            PluginConfiguration restored;
            using (var reader = new StringReader(xml))
            {
                restored = (PluginConfiguration)serializer.Deserialize(reader);
            }

            var room = Assert.Single(restored.WatchParties);
            Assert.Equal("legacy-room", room.Id);
            Assert.Equal("Legacy room", room.ItemName);
            Assert.Equal("master-user", room.MasterUserId);
            Assert.Equal(7, room.SyncToleranceSeconds);
            Assert.Equal(12, room.MaxParticipants);
        }
    }
}
