using System;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class EmbyServerAddressTests
    {
        [Fact]
        public void UsesDefaultAddressWhenConfigurationIsEmpty()
        {
            Assert.Equal(
                "http://localhost:8096/emby/System/Info/Public",
                EmbyServerAddress.Build(null, "/emby/System/Info/Public"));
        }

        [Fact]
        public void BuildsAddressUsingConfiguredNonStandardPort()
        {
            Assert.Equal(
                "http://localhost:6908/emby/Items/123/Refresh?Recursive=true",
                EmbyServerAddress.Build(" http://localhost:6908/ ", "emby/Items/123/Refresh?Recursive=true"));
        }

        [Fact]
        public void PreservesConfiguredBasePath()
        {
            Assert.Equal(
                "https://example.test/emby-base/emby/System/Info/Public",
                EmbyServerAddress.Build("https://example.test/emby-base", "emby/System/Info/Public"));
        }

        [Theory]
        [InlineData("localhost:6908")]
        [InlineData("file:///tmp/emby")]
        [InlineData("https://example.test?token=secret")]
        [InlineData("https://example.test/#fragment")]
        public void RejectsInvalidServerAddress(string configuredUrl)
        {
            Assert.Throws<ArgumentException>(() => EmbyServerAddress.GetBaseUrl(configuredUrl));
        }
    }
}
