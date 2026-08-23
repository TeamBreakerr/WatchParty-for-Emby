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
        public void MatchesAConcreteMovieVersionToItsLogicalMovieParty()
        {
            var party = new WatchPartyItem
            {
                Id = "bouquet-party",
                ItemId = "2188714",
                IsActive = true
            };
            var partyItem = new WatchPartyMediaIdentity
            {
                InternalItemId = 2188714,
                PresentationUniqueKey = "b225fbeb90004720b1be420af6ebe338_"
            };
            var playingVersion = new WatchPartyMediaIdentity
            {
                InternalItemId = 2276737,
                PresentationUniqueKey = "b225fbeb90004720b1be420af6ebe338_"
            };

            var match = WatchPartyItemMatcher.FindActiveParty(
                new[] { party },
                playingVersion,
                configuredItemId => configuredItemId == party.ItemId ? partyItem : null);

            Assert.Same(party, match);
        }

        [Fact]
        public void MatchesAConcreteMovieVersionFromTheParentsMediaSources()
        {
            var party = new WatchPartyItem
            {
                Id = "movie-party",
                ItemId = "2188714",
                IsActive = true
            };

            var match = WatchPartyItemMatcher.FindActiveParty(
                new[] { party },
                new WatchPartyMediaIdentity { InternalItemId = 2276737 },
                _ => new WatchPartyMediaIdentity
                {
                    InternalItemId = 2188714,
                    MediaSourceItemIds = new[] { "mediasource_2276737" }
                });

            Assert.Same(party, match);
        }

        [Fact]
        public void DoesNotMatchDifferentLogicalMovies()
        {
            var party = new WatchPartyItem
            {
                Id = "bouquet-party",
                ItemId = "2188714",
                IsActive = true
            };

            var match = WatchPartyItemMatcher.FindActiveParty(
                new[] { party },
                new WatchPartyMediaIdentity
                {
                    InternalItemId = 9000000,
                    PresentationUniqueKey = "another-movie"
                },
                _ => new WatchPartyMediaIdentity
                {
                    InternalItemId = 2188714,
                    PresentationUniqueKey = "b225fbeb90004720b1be420af6ebe338_"
                });

            Assert.Null(match);
        }

        [Fact]
        public void RejectsAmbiguousEquivalentMovieRooms()
        {
            var firstParty = new WatchPartyItem
            {
                Id = "first",
                ItemId = "2188714",
                IsActive = true
            };
            var secondParty = new WatchPartyItem
            {
                Id = "second",
                ItemId = "2831958",
                IsActive = true
            };
            var playingVersion = new WatchPartyMediaIdentity
            {
                InternalItemId = 2276737,
                PresentationUniqueKey = "b225fbeb90004720b1be420af6ebe338_"
            };

            var match = WatchPartyItemMatcher.FindActiveParty(
                new[] { firstParty, secondParty },
                playingVersion,
                configuredItemId => new WatchPartyMediaIdentity
                {
                    InternalItemId = long.Parse(configuredItemId),
                    PresentationUniqueKey = "b225fbeb90004720b1be420af6ebe338_"
                });

            Assert.Null(match);
        }

        [Fact]
        public void ExactMovieRoomWinsOverAnEquivalentRoom()
        {
            var exactParty = new WatchPartyItem
            {
                Id = "exact",
                ItemId = "2276737",
                IsActive = true
            };
            var equivalentParty = new WatchPartyItem
            {
                Id = "equivalent",
                ItemId = "2188714",
                IsActive = true
            };

            var match = WatchPartyItemMatcher.FindActiveParty(
                new[] { equivalentParty, exactParty },
                new WatchPartyMediaIdentity
                {
                    InternalItemId = 2276737,
                    PresentationUniqueKey = "b225fbeb90004720b1be420af6ebe338_"
                },
                configuredItemId => new WatchPartyMediaIdentity
                {
                    InternalItemId = long.Parse(configuredItemId),
                    PresentationUniqueKey = "b225fbeb90004720b1be420af6ebe338_"
                });

            Assert.Same(exactParty, match);
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
        public void LogicalMovieMatchingDoesNotChangeSeriesEpisodeMatching()
        {
            var party = new WatchPartyItem
            {
                Id = "series-party",
                ItemId = "1001",
                IsSeriesParty = true,
                IsActive = true,
                EpisodeQueue = new List<WatchPartyEpisode>
                {
                    Episode("1001", 1, 1),
                    Episode("1002", 1, 2)
                }
            };

            var match = WatchPartyItemMatcher.FindActiveParty(
                new[] { party },
                new WatchPartyMediaIdentity
                {
                    InternalItemId = 1002,
                    PresentationUniqueKey = "episode-two"
                },
                _ => new WatchPartyMediaIdentity
                {
                    InternalItemId = 9999,
                    PresentationUniqueKey = "episode-two"
                });

            Assert.Same(party, match);
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
