using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class ExternalWebServerBindingTests
    {
        [Fact]
        public void ProtocolOrReverseProxyChangesRequireANewListener()
        {
            var configuration = Configuration();
            var original = ExternalWebServerBinding.From(configuration);

            configuration.EnableHttps = true;
            Assert.NotEqual(original, ExternalWebServerBinding.From(configuration));

            configuration.EnableHttps = false;
            configuration.UseReverseProxy = true;
            Assert.NotEqual(original, ExternalWebServerBinding.From(configuration));
        }

        [Fact]
        public void BlankAndDefaultLoopbackAddressesHaveTheSameBinding()
        {
            var blank = Configuration();
            blank.ListenAddress = "  ";
            var loopback = Configuration();
            loopback.ListenAddress = "127.0.0.1";

            Assert.Equal(
                ExternalWebServerBinding.From(blank),
                ExternalWebServerBinding.From(loopback));
        }

        private static PluginConfiguration Configuration()
        {
            return new PluginConfiguration
            {
                EnableExternalWebServer = true,
                ExternalWebServerPort = 8097,
                ListenAddress = "127.0.0.1",
                UseReverseProxy = false,
                EnableHttps = false
            };
        }
    }
}
