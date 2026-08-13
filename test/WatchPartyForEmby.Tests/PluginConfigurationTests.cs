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
    }
}
