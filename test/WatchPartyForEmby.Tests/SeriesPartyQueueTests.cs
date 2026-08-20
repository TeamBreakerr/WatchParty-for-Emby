using System;
using System.Collections.Generic;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class SeriesPartyQueueTests
    {
        [Fact]
        public void RepairOrdersEpisodesAndKeepsTheSelectedStartingEpisode()
        {
            var party = new WatchPartyItem
            {
                IsSeriesParty = true,
                SeriesName = "Example Series",
                CurrentEpisodeId = "s1e2",
                EpisodeQueue = new List<WatchPartyEpisode>
                {
                    Episode("s2e1", 2, 1),
                    Episode("s1e2", 1, 2),
                    Episode("s1e1", 1, 1)
                }
            };

            var changed = SeriesPartyQueue.Repair(party);

            Assert.True(changed);
            Assert.True(party.IsSeriesParty);
            Assert.Equal(new[] { "s1e1", "s1e2", "s2e1" }, party.EpisodeQueue.ConvertAll(e => e.ItemId));
            Assert.Equal(1, party.CurrentEpisodeIndex);
            Assert.Equal("s1e2", party.CurrentEpisodeId);
            Assert.Equal("s1e2", party.ItemId);
            Assert.Equal("Example Series", party.SeriesName);
        }

        [Fact]
        public void PlaybackStopNeverSelectsTheNextEpisode()
        {
            var party = CreatePartyAtFirstEpisode();
            party.CurrentPositionTicks = TimeSpan.FromMinutes(22).Ticks - TimeSpan.FromSeconds(10).Ticks;
            party.IsPlaying = true;

            var result = SeriesPartyQueue.TryAdvanceAfterStop(
                party,
                "s1e1",
                party.CurrentPositionTicks,
                TimeSpan.FromMinutes(22).Ticks);

            Assert.Equal(SeriesPartyAdvanceResult.NotCompleted, result);
            Assert.Equal(0, party.CurrentEpisodeIndex);
            Assert.Equal("s1e1", party.CurrentEpisodeId);
            Assert.Equal("s1e1", party.ItemId);
            Assert.Equal(TimeSpan.FromMinutes(22).Ticks - TimeSpan.FromSeconds(10).Ticks, party.CurrentPositionTicks);
            Assert.True(party.IsPlaying);
        }

        [Fact]
        public void ManualStopDoesNotAdvance()
        {
            var party = CreatePartyAtFirstEpisode();

            var result = SeriesPartyQueue.TryAdvanceAfterStop(
                party,
                "s1e1",
                TimeSpan.FromMinutes(5).Ticks,
                TimeSpan.FromMinutes(22).Ticks);

            Assert.Equal(SeriesPartyAdvanceResult.NotCompleted, result);
            Assert.Equal(0, party.CurrentEpisodeIndex);
            Assert.Equal("s1e1", party.CurrentEpisodeId);
        }

        [Fact]
        public void SelectingQueuedEpisodeMovesTheRoomAndResetsProgress()
        {
            var party = CreatePartyAtFirstEpisode();
            party.CurrentPositionTicks = TimeSpan.FromMinutes(5).Ticks;
            party.IsPlaying = true;

            var selected = SeriesPartyQueue.TrySelectEpisode(party, "s1e2");

            Assert.True(selected);
            Assert.Equal(1, party.CurrentEpisodeIndex);
            Assert.Equal("s1e2", party.CurrentEpisodeId);
            Assert.Equal("s1e2", party.ItemId);
            Assert.Equal(0, party.CurrentPositionTicks);
            Assert.False(party.IsPlaying);
        }

        [Fact]
        public void SelectingEpisodeOutsideQueueDoesNotChangeTheRoom()
        {
            var party = CreatePartyAtFirstEpisode();

            var selected = SeriesPartyQueue.TrySelectEpisode(party, "not-in-party");

            Assert.False(selected);
            Assert.Equal(0, party.CurrentEpisodeIndex);
            Assert.Equal("s1e1", party.CurrentEpisodeId);
            Assert.Equal("s1e1", party.ItemId);
        }

        [Fact]
        public void RemovingAMissingCurrentEpisodeSelectsTheNextAvailableEpisode()
        {
            var party = new WatchPartyItem
            {
                IsSeriesParty = true,
                CurrentEpisodeId = "s1e2",
                CurrentEpisodeIndex = 1,
                ItemId = "s1e2",
                CurrentPositionTicks = TimeSpan.FromMinutes(20).Ticks,
                IsPlaying = true,
                EpisodeQueue = new List<WatchPartyEpisode>
                {
                    Episode("s1e1", 1, 1),
                    Episode("s1e2", 1, 2),
                    Episode("s1e3", 1, 3)
                }
            };

            var changed = SeriesPartyQueue.RemoveUnavailableEpisodes(
                party,
                itemId => itemId != "s1e2");

            Assert.True(changed);
            Assert.Equal(new[] { "s1e1", "s1e3" }, party.EpisodeQueue.ConvertAll(e => e.ItemId));
            Assert.Equal(1, party.CurrentEpisodeIndex);
            Assert.Equal("s1e3", party.CurrentEpisodeId);
            Assert.Equal("s1e3", party.ItemId);
            Assert.Equal(0, party.CurrentPositionTicks);
            Assert.False(party.IsPlaying);
        }

        [Fact]
        public void RemovingAnEarlierMissingEpisodeKeepsTheCurrentEpisode()
        {
            var party = new WatchPartyItem
            {
                IsSeriesParty = true,
                CurrentEpisodeId = "s1e2",
                CurrentEpisodeIndex = 1,
                ItemId = "s1e2",
                EpisodeQueue = new List<WatchPartyEpisode>
                {
                    Episode("s1e1", 1, 1),
                    Episode("s1e2", 1, 2)
                }
            };

            var changed = SeriesPartyQueue.RemoveUnavailableEpisodes(
                party,
                itemId => itemId != "s1e1");

            Assert.True(changed);
            Assert.Single(party.EpisodeQueue);
            Assert.Equal(0, party.CurrentEpisodeIndex);
            Assert.Equal("s1e2", party.CurrentEpisodeId);
        }

        [Fact]
        public void RemovingTheLastCurrentEpisodeSelectsThePreviousEpisode()
        {
            var party = new WatchPartyItem
            {
                IsSeriesParty = true,
                CurrentEpisodeId = "s1e3",
                CurrentEpisodeIndex = 2,
                ItemId = "s1e3",
                CurrentPositionTicks = TimeSpan.FromMinutes(20).Ticks,
                IsPlaying = true,
                EpisodeQueue = new List<WatchPartyEpisode>
                {
                    Episode("s1e1", 1, 1),
                    Episode("s1e2", 1, 2),
                    Episode("s1e3", 1, 3)
                }
            };

            var changed = SeriesPartyQueue.RemoveUnavailableEpisodes(
                party,
                itemId => itemId != "s1e3");

            Assert.True(changed);
            Assert.Equal(1, party.CurrentEpisodeIndex);
            Assert.Equal("s1e2", party.CurrentEpisodeId);
            Assert.Equal("s1e2", party.ItemId);
            Assert.Equal(0, party.CurrentPositionTicks);
            Assert.False(party.IsPlaying);
        }

        [Fact]
        public void LegacyPartyRemainsASingleItemPartyByDefault()
        {
            var party = new WatchPartyItem();

            Assert.False(party.IsSeriesParty);
            Assert.Empty(party.EpisodeQueue);
            Assert.Equal(-1, party.CurrentEpisodeIndex);
        }

        private static WatchPartyItem CreatePartyAtFirstEpisode()
        {
            var party = new WatchPartyItem
            {
                IsSeriesParty = true,
                SeriesName = "Example Series",
                CurrentEpisodeId = "s1e1",
                EpisodeQueue = new List<WatchPartyEpisode>
                {
                    Episode("s1e1", 1, 1),
                    Episode("s1e2", 1, 2)
                }
            };
            SeriesPartyQueue.Repair(party);
            return party;
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
