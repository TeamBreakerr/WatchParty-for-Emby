using System.Collections.Generic;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class PartyEpisodeSelectionCoordinatorTests
    {
        [Theory]
        [InlineData(true, false)]
        [InlineData(true, true)]
        [InlineData(false, false)]
        public void WaitingOrPausedRoomOnlySelectsTheEpisode(
            bool isWaitingRoom,
            bool isPlaying)
        {
            var party = Party(isWaitingRoom, isPlaying);

            var commit = PartyEpisodeSelectionCoordinator.TryCommit(
                party,
                "episode-2",
                Context(isWaitingRoom, isPlaying, false, false));

            Assert.True(commit.Accepted);
            Assert.False(commit.ShouldDispatch);
            Assert.Equal("episode-2", party.CurrentEpisodeId);
            Assert.Equal(0, party.CurrentPositionTicks);
            Assert.False(party.IsPlaying);
        }

        [Fact]
        public void PlayingRoomWithoutActiveMasterIsRejectedWithoutMutation()
        {
            var party = Party(isWaitingRoom: false, isPlaying: true);

            var commit = PartyEpisodeSelectionCoordinator.TryCommit(
                party,
                "episode-2",
                Context(false, true, false, true));

            Assert.False(commit.Accepted);
            Assert.Equal(
                PartyEpisodeSelectionDisposition.RejectNoActiveMaster,
                commit.Disposition);
            Assert.Equal("episode-1", party.CurrentEpisodeId);
            Assert.True(party.IsPlaying);
        }

        [Fact]
        public void PlayingRoomWithUncontrollableMasterIsRejectedWithoutMutation()
        {
            var party = Party(isWaitingRoom: false, isPlaying: true);

            var commit = PartyEpisodeSelectionCoordinator.TryCommit(
                party,
                "episode-2",
                Context(false, true, true, false));

            Assert.False(commit.Accepted);
            Assert.Equal(
                PartyEpisodeSelectionDisposition.RejectMasterNotControllable,
                commit.Disposition);
            Assert.Equal("episode-1", party.CurrentEpisodeId);
        }

        [Fact]
        public void PlayingRoomWithControllableMasterCommitsDispatchState()
        {
            var party = Party(isWaitingRoom: false, isPlaying: true);

            var commit = PartyEpisodeSelectionCoordinator.TryCommit(
                party,
                "episode-2",
                Context(false, true, true, true));

            Assert.True(commit.Accepted);
            Assert.True(commit.ShouldDispatch);
            Assert.Equal("episode-2", commit.Episode.ItemId);
            Assert.Equal("episode-2", party.CurrentEpisodeId);
            Assert.True(party.IsPlaying);
        }

        [Fact]
        public void CurrentOrUnqueuedEpisodeNeverMutatesTheRoom()
        {
            var party = Party(isWaitingRoom: false, isPlaying: true);

            var current = PartyEpisodeSelectionCoordinator.TryCommit(
                party,
                "episode-1",
                Context(false, true, true, true));
            var missing = PartyEpisodeSelectionCoordinator.TryCommit(
                party,
                "episode-404",
                Context(false, true, true, true));

            Assert.Equal(PartyEpisodeSelectionDisposition.AlreadyCurrent, current.Disposition);
            Assert.Equal(
                PartyEpisodeSelectionDisposition.RejectEpisodeNotQueued,
                missing.Disposition);
            Assert.Equal("episode-1", party.CurrentEpisodeId);
        }

        [Fact]
        public void UnavailableQueuedEpisodeNeverMutatesTheRoom()
        {
            var party = Party(isWaitingRoom: false, isPlaying: true);
            var context = Context(false, true, true, true);
            context.IsEpisodeAvailable = false;

            var commit = PartyEpisodeSelectionCoordinator.TryCommit(
                party,
                "episode-2",
                context);

            Assert.False(commit.Accepted);
            Assert.Equal(
                PartyEpisodeSelectionDisposition.RejectEpisodeUnavailable,
                commit.Disposition);
            Assert.Equal("episode-1", party.CurrentEpisodeId);
            Assert.True(party.IsPlaying);
        }

        private static WatchPartyItem Party(bool isWaitingRoom, bool isPlaying)
        {
            return new WatchPartyItem
            {
                IsSeriesParty = true,
                IsWaitingRoom = isWaitingRoom,
                IsPlaying = isPlaying,
                CurrentEpisodeId = "episode-1",
                CurrentEpisodeIndex = 0,
                ItemId = "episode-1",
                CurrentPositionTicks = 300,
                EpisodeQueue = new List<WatchPartyEpisode>
                {
                    new WatchPartyEpisode { ItemId = "episode-1", ItemName = "First" },
                    new WatchPartyEpisode { ItemId = "episode-2", ItemName = "Second" }
                }
            };
        }

        private static PartyEpisodeSelectionContext Context(
            bool waiting,
            bool playing,
            bool hasMaster,
            bool controllable)
        {
            return new PartyEpisodeSelectionContext
            {
                IsWaitingRoom = waiting,
                IsPlaying = playing,
                IsEpisodeAvailable = true,
                HasActiveMaster = hasMaster,
                MasterCanReceivePlayback = controllable
            };
        }
    }
}
