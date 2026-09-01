using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class PluginConfigurationPolicyTests
    {
        [Fact]
        public void LegacyFixedResumeOffsetIsDisabled()
        {
            var configuration = new PluginConfiguration
            {
                SyncOffsetMilliseconds = 1000
            };

            Assert.True(PluginConfigurationPolicy.Normalize(configuration));

            Assert.Equal(0, configuration.SyncOffsetMilliseconds);
        }

        [Fact]
        public void MissingOrUnsafeValuesReceiveBoundedDefaults()
        {
            var configuration = new PluginConfiguration
            {
                SyncIntervalSeconds = 0,
                SyncOffsetMilliseconds = 50000
            };

            Assert.True(PluginConfigurationPolicy.Normalize(configuration));
            Assert.Equal(5, configuration.SyncIntervalSeconds);
            Assert.Equal(0, configuration.SyncOffsetMilliseconds);
        }

        [Fact]
        public void NormalizationAlsoAppliesRoomInvariantsAndIsIdempotent()
        {
            var configuration = new PluginConfiguration();
            configuration.WatchParties.Add(new WatchPartyItem
            {
                MasterUserId = "master",
                HostUserId = null,
                MaxParticipants = 500
            });

            Assert.True(PluginConfigurationPolicy.Normalize(configuration));
            Assert.Equal("master", configuration.WatchParties[0].HostUserId);
            Assert.Equal(100, configuration.WatchParties[0].MaxParticipants);
            Assert.False(PluginConfigurationPolicy.Normalize(configuration));
        }
    }
}
