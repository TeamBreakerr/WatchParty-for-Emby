using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class PlaybackControlCapabilitiesTests
    {
        [Theory]
        [InlineData("Emby Web")]
        [InlineData("Emby for iOS")]
        [InlineData("Emby Theater")]
        public void OfficialEmbyClientsAllowPauseDuringTransientCapabilityReload(string client)
        {
            Assert.True(PlaybackControlCapabilities.CanReceivePauseState(
                client,
                supportsRemoteControl: false));
        }

        [Theory]
        [InlineData("VidHub")]
        [InlineData("Conflux")]
        [InlineData(null)]
        public void ThirdPartyClientsMustExplicitlySupportRemoteControl(string client)
        {
            Assert.False(PlaybackControlCapabilities.CanReceivePauseState(
                client,
                supportsRemoteControl: false));
        }

        [Fact]
        public void ExplicitRemoteControlSupportAlwaysAllowsPause()
        {
            Assert.True(PlaybackControlCapabilities.CanReceivePauseState(
                "Conflux",
                supportsRemoteControl: true));
        }
    }
}
