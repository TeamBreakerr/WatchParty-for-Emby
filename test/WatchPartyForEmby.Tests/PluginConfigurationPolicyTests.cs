using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class PluginConfigurationPolicyTests
    {
        [Fact]
        public void LegitimateZeroValuesArePreserved()
        {
            var configuration = new PluginConfiguration
            {
                SyncOffsetMilliseconds = 0,
                RateLimitRequestsPerMinute = 0,
                HstsMaxAge = 0,
                MaxAuditLogEntries = 0
            };

            PluginConfigurationPolicy.Normalize(configuration);

            Assert.Equal(0, configuration.SyncOffsetMilliseconds);
            Assert.Equal(0, configuration.RateLimitRequestsPerMinute);
            Assert.Equal(0, configuration.HstsMaxAge);
            Assert.Equal(0, configuration.MaxAuditLogEntries);
        }

        [Fact]
        public void MissingOrUnsafeValuesReceiveBoundedDefaults()
        {
            var configuration = new PluginConfiguration
            {
                SyncIntervalSeconds = 0,
                SyncOffsetMilliseconds = 50000,
                ExternalWebServerPort = 0,
                ListenAddress = " ",
                SessionExpirationMinutes = -1,
                RateLimitRequestsPerMinute = -1,
                HstsMaxAge = -1,
                MaxFailedLoginAttempts = 0
            };

            Assert.True(PluginConfigurationPolicy.Normalize(configuration));
            Assert.Equal(5, configuration.SyncIntervalSeconds);
            Assert.Equal(10000, configuration.SyncOffsetMilliseconds);
            Assert.Equal(8097, configuration.ExternalWebServerPort);
            Assert.Equal("127.0.0.1", configuration.ListenAddress);
            Assert.Equal(5, configuration.SessionExpirationMinutes);
            Assert.Equal(0, configuration.RateLimitRequestsPerMinute);
            Assert.Equal(0, configuration.HstsMaxAge);
            Assert.Equal(5, configuration.MaxFailedLoginAttempts);
        }

        [Fact]
        public void NormalizationAlsoAppliesRoomInvariantsAndIsIdempotent()
        {
            var configuration = new PluginConfiguration();
            configuration.WatchParties.Add(new WatchPartyItem
            {
                MasterUserId = "master",
                HostUserId = null,
                PauseControl = "MasterOnly",
                MaxParticipants = 500
            });

            Assert.True(PluginConfigurationPolicy.Normalize(configuration));
            Assert.Equal("master", configuration.WatchParties[0].HostUserId);
            Assert.Equal("Host", configuration.WatchParties[0].PauseControl);
            Assert.Equal(100, configuration.WatchParties[0].MaxParticipants);
            Assert.False(PluginConfigurationPolicy.Normalize(configuration));
        }
    }
}
