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
        public void NaturalCompletionAdvancesExactlyOneEpisodeAndResetsProgress()
        {
            var party = CreatePartyAtFirstEpisode();
            party.CurrentPositionTicks = TimeSpan.FromMinutes(22).Ticks - TimeSpan.FromSeconds(10).Ticks;
            party.IsPlaying = true;

            var result = SeriesPartyQueue.TryAdvanceAfterStop(
                party,
                "s1e1",
                party.CurrentPositionTicks,
                TimeSpan.FromMinutes(22).Ticks);

            Assert.Equal(SeriesPartyAdvanceResult.Advanced, result);
            Assert.Equal(1, party.CurrentEpisodeIndex);
            Assert.Equal("s1e2", party.CurrentEpisodeId);
            Assert.Equal("s1e2", party.ItemId);
            Assert.Equal(0, party.CurrentPositionTicks);
            Assert.False(party.IsPlaying);
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
        public void StopAtNinetyFivePercentButOutsideCompletionWindowDoesNotAdvance()
        {
            var party = CreatePartyAtFirstEpisode();

            var result = SeriesPartyQueue.TryAdvanceAfterStop(
                party,
                "s1e1",
                TimeSpan.FromMinutes(21).Ticks,
                TimeSpan.FromMinutes(22).Ticks);

            Assert.Equal(SeriesPartyAdvanceResult.NotCompleted, result);
            Assert.Equal("s1e1", party.CurrentEpisodeId);
        }

        [Fact]
        public void DuplicateStopFromPreviousEpisodeDoesNotSkipAnEpisode()
        {
            var party = CreatePartyAtFirstEpisode();
            var runtime = TimeSpan.FromMinutes(22).Ticks;

            Assert.Equal(
                SeriesPartyAdvanceResult.Advanced,
                SeriesPartyQueue.TryAdvanceAfterStop(party, "s1e1", runtime, runtime));

            Assert.Equal(
                SeriesPartyAdvanceResult.ItemMismatch,
                SeriesPartyQueue.TryAdvanceAfterStop(party, "s1e1", runtime, runtime));
            Assert.Equal(1, party.CurrentEpisodeIndex);
            Assert.Equal("s1e2", party.CurrentEpisodeId);
        }

        [Fact]
        public void CompletingLastEpisodeMarksQueueCompleteWithoutMovingPastEnd()
        {
            var party = CreatePartyAtFirstEpisode();
            party.CurrentEpisodeIndex = 1;
            party.CurrentEpisodeId = "s1e2";
            party.ItemId = "s1e2";

            var result = SeriesPartyQueue.TryAdvanceAfterStop(
                party,
                "s1e2",
                TimeSpan.FromMinutes(22).Ticks,
                TimeSpan.FromMinutes(22).Ticks);

            Assert.Equal(SeriesPartyAdvanceResult.EndOfQueue, result);
            Assert.Equal(1, party.CurrentEpisodeIndex);
            Assert.Equal("s1e2", party.CurrentEpisodeId);
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
