using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class PlaybackMediaSourceSelectorTests
    {
        [Fact]
        public void ConfiguredVersionWinsWhenItBelongsToTheEpisode()
        {
            Assert.Equal(
                "configured-source",
                PlaybackMediaSourceSelector.Resolve(
                    "configured-source",
                    new[] { "default-source", "configured-source" }));
        }

        [Fact]
        public void StaleConfiguredVersionLetsEmbyResolveTheCurrentDefault()
        {
            Assert.Null(PlaybackMediaSourceSelector.Resolve(
                "old-strm-source",
                new[] { "local-episode-source" }));
        }

        [Fact]
        public void AnUnconfiguredEpisodeLetsEmbyResolveItsDefaultSource()
        {
            Assert.Null(PlaybackMediaSourceSelector.Resolve(
                null,
                new[] { null, " ", "mediasource_local_episode" }));
        }

        [Fact]
        public void UnknownSourcesOmitTheIdentifierAndLetEmbyResolveIt()
        {
            Assert.Null(PlaybackMediaSourceSelector.Resolve(
                "unverified-source",
                availableMediaSourceIds: null));
        }
    }
}
