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

        [Theory]
        [InlineData("localhost:6908")]
        [InlineData("file:///tmp/emby")]
        public void RejectsInvalidServerAddress(string configuredUrl)
        {
            Assert.Throws<ArgumentException>(() => EmbyServerAddress.GetBaseUrl(configuredUrl));
        }
    }
}
