using System.IO;
using System.Xml.Serialization;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class PluginConfigurationTests
    {
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
        public void EmbeddedControlCenterUpgradePreservesRoomsAndIsIdempotent()
        {
            var room = new WatchPartyItem { Id = "preserved-room" };
            var configuration = new PluginConfiguration
            {
                ConfigurationVersion =
                    PluginConfigurationMigration.DirectItemBindingVersion
            };
            configuration.WatchParties.Add(room);

            Assert.True(
                PluginConfigurationMigration.UpgradeToEmbeddedControlCenter(
                    configuration));
            Assert.Equal(
                PluginConfigurationMigration.EmbeddedControlCenterVersion,
                configuration.ConfigurationVersion);
            Assert.Same(room, Assert.Single(configuration.WatchParties));
            Assert.False(
                PluginConfigurationMigration.UpgradeToEmbeddedControlCenter(
                    configuration));
        }

        [Fact]
        public void EmbeddedControlCenterUpgradeRunsEarlierSchemaStepsFirst()
        {
            var configuration = new PluginConfiguration
            {
                ConfigurationVersion = 0,
                WatchParties = null
            };

            Assert.True(
                PluginConfigurationMigration.UpgradeToEmbeddedControlCenter(
                    configuration));

            Assert.Equal(
                PluginConfigurationMigration.EmbeddedControlCenterVersion,
                configuration.ConfigurationVersion);
            Assert.NotNull(configuration.WatchParties);
            Assert.Empty(configuration.WatchParties);
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

        [Fact]
        public void RetiredExternalConsoleElementsAreIgnoredByXmlConfiguration()
        {
            const string xml =
                "<?xml version=\"1.0\" encoding=\"utf-16\"?>" +
                "<PluginConfiguration xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" " +
                "xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">" +
                "<EnableExternalWebServer>true</EnableExternalWebServer>" +
                "<ExternalWebServerPort>8097</ExternalWebServerPort>" +
                "<EmbyApiKey>retired-secret</EmbyApiKey>" +
                "<SyncIntervalSeconds>8</SyncIntervalSeconds>" +
                "<WatchParties />" +
                "</PluginConfiguration>";
            var serializer = new XmlSerializer(typeof(PluginConfiguration));

            PluginConfiguration restored;
            using (var reader = new StringReader(xml))
            {
                restored = (PluginConfiguration)serializer.Deserialize(reader);
            }

            Assert.Equal(8, restored.SyncIntervalSeconds);
            Assert.Empty(restored.WatchParties);

            string rewrittenXml;
            using (var writer = new StringWriter())
            {
                serializer.Serialize(writer, restored);
                rewrittenXml = writer.ToString();
            }
            Assert.DoesNotContain("EnableExternalWebServer", rewrittenXml);
            Assert.DoesNotContain("ExternalWebServerPort", rewrittenXml);
            Assert.DoesNotContain("EmbyApiKey", rewrittenXml);
        }
    }
}
