using System;
using System.Collections.Generic;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class WatchPartyItemMatcherTests
    {
        [Fact]
        public void MatchesASingleItemPartyByTheOriginalInternalId()
        {
            var party = new WatchPartyItem
            {
                Id = "movie-party",
                ItemId = "1234",
                IsActive = true
            };

            var match = WatchPartyItemMatcher.FindActiveParty(
                new[] { party },
                Guid.NewGuid(),
                1234);

            Assert.Same(party, match);
        }

        [Fact]
        public void MatchesAnyOriginalEpisodeInAWholeSeriesParty()
        {
            var party = new WatchPartyItem
            {
                Id = "series-party",
                ItemId = "1001",
                CurrentEpisodeId = "1001",
                CurrentEpisodeIndex = 0,
                IsSeriesParty = true,
                IsActive = true,
                EpisodeQueue = new List<WatchPartyEpisode>
                {
                    Episode("1001", 1, 1),
                    Episode("1002", 1, 2),
                    Episode("2001", 2, 1)
                }
            };

            var match = WatchPartyItemMatcher.FindActiveParty(
                new[] { party },
                Guid.NewGuid(),
                2001);

            Assert.Same(party, match);
            Assert.Equal(
                "2001",
                WatchPartyItemMatcher.FindEpisodeItemId(party, Guid.NewGuid(), 2001));
        }

        [Fact]
        public void SkipsAnInactiveDuplicateAndReturnsTheActiveParty()
        {
            var inactiveParty = new WatchPartyItem
            {
                Id = "inactive",
                ItemId = "1234",
                IsActive = false
            };
            var activeParty = new WatchPartyItem
            {
                Id = "active",
                ItemId = "1234",
                IsActive = true
            };

            var match = WatchPartyItemMatcher.FindActiveParty(
                new[] { inactiveParty, activeParty },
                Guid.NewGuid(),
                1234);

            Assert.Same(activeParty, match);
        }

        [Fact]
        public void RejectsAmbiguousActiveMovieBindings()
        {
            var firstParty = new WatchPartyItem
            {
                Id = "first",
                ItemId = "1234",
                IsActive = true
            };
            var secondParty = new WatchPartyItem
            {
                Id = "second",
                ItemId = "001234",
                IsActive = true
            };

            var parties = new[] { firstParty, secondParty };

            Assert.True(WatchPartyItemMatcher.HasActiveBindingConflict(parties));
            Assert.Null(WatchPartyItemMatcher.FindActiveParty(parties, Guid.NewGuid(), 1234));
        }

        [Fact]
        public void DetectsOverlappingActiveSeriesQueues()
        {
            var firstParty = new WatchPartyItem
            {
                Id = "first-series",
                ItemId = "1001",
                IsSeriesParty = true,
                IsActive = true,
                EpisodeQueue = new List<WatchPartyEpisode>
                {
                    Episode("1001", 1, 1),
                    Episode("1002", 1, 2)
                }
            };
            var secondParty = new WatchPartyItem
            {
                Id = "second-series",
                ItemId = "1002",
                IsSeriesParty = true,
                IsActive = true,
                EpisodeQueue = new List<WatchPartyEpisode>
                {
                    Episode("1002", 1, 2),
                    Episode("1003", 1, 3)
                }
            };

            Assert.True(WatchPartyItemMatcher.HasActiveBindingConflict(new[] { firstParty, secondParty }));
        }

        private static WatchPartyEpisode Episode(string itemId, int season, int episode)
        {
            return new WatchPartyEpisode
            {
                ItemId = itemId,
                ItemName = itemId,
                SeasonId = $"season-{season}",
                SeasonNumber = season,
                EpisodeNumber = episode
            };
        }
    }
}
