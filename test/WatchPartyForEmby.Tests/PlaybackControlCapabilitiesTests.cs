using Xunit;
using MediaBrowser.Controller.Session;

namespace WatchPartyForEmby.Tests
{
    public sealed class PlaybackControlCapabilitiesTests
    {
        [Fact]
        public void MissingRemoteControlNeverReportsACommandAsDeliverable()
        {
            Assert.False(PlaybackControlCapabilities.CanReceivePlaybackCommand(
                supportsRemoteControl: false,
                playableMediaTypes: new[] { "Video" }));
        }

        [Fact]
        public void MissingVideoCapabilityNeverReportsACommandAsDeliverable()
        {
            Assert.False(PlaybackControlCapabilities.CanReceivePlaybackCommand(
                supportsRemoteControl: true,
                playableMediaTypes: new[] { "Audio" }));
        }

        [Fact]
        public void RemoteVideoCapabilityAllowsPlaybackCommands()
        {
            Assert.True(PlaybackControlCapabilities.CanReceivePlaybackCommand(
                supportsRemoteControl: true,
                playableMediaTypes: new[] { "Audio", "video" }));
        }

        [Fact]
        public void RetainedSessionWithoutCapabilitiesIsSafelyNotControllable()
        {
            Assert.False(PlaybackControlCapabilities.SessionSupportsRemoteControl(
                new SessionInfo()));
        }
    }
}
