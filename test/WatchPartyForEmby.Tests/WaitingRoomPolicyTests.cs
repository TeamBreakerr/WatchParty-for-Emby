using System.Collections.Generic;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class WaitingRoomPolicyTests
    {
        [Fact]
        public void ReachingMinimumReadyCountStartsWithoutWaitingForOfflineWhitelistUsers()
        {
            var party = WaitingParty(minReadyCount: 2);
            party.AllowedUserIds = new List<string>
            {
                "master",
                "viewer-a",
                "offline-viewer"
            };

            Assert.True(WaitingRoomPolicy.ShouldAutoStart(party, readyUserCount: 2));
        }

        [Theory]
        [InlineData(1, true, false, true)]
        [InlineData(2, false, false, true)]
        [InlineData(2, true, true, true)]
        [InlineData(2, true, false, false)]
        public void StartRequiresThresholdWaitingStateAndAutoStart(
            int readyCount,
            bool isWaitingRoom,
            bool isPlaying,
            bool autoStart)
        {
            var party = WaitingParty(minReadyCount: 2);
            party.IsWaitingRoom = isWaitingRoom;
            party.IsPlaying = isPlaying;
            party.AutoStartWhenReady = autoStart;

            Assert.False(WaitingRoomPolicy.ShouldAutoStart(party, readyCount));
        }

        [Fact]
        public void PlaybackProgressCannotBypassTheWaitingRoomBeforeStart()
        {
            var party = WaitingParty(minReadyCount: 2);

            Assert.True(WaitingRoomPolicy.SuppressesPlaybackControl(party));
            Assert.True(WaitingRoomPolicy.ShouldRestorePause(
                party,
                reportedIsPaused: false));

            party.IsWaitingRoom = false;
            party.IsPlaying = true;

            Assert.False(WaitingRoomPolicy.SuppressesPlaybackControl(party));
            Assert.False(WaitingRoomPolicy.ShouldRestorePause(
                party,
                reportedIsPaused: false));
        }

        [Fact]
        public void OfflineReadyUsersDoNotCountTowardTheStartThreshold()
        {
            Assert.Equal(1, WaitingRoomPolicy.CountPresentReadyUsers(
                new[] { "online-user", "offline-user" },
                new[] { "ONLINE-USER" }));
        }

        private static WatchPartyItem WaitingParty(int minReadyCount)
        {
            return new WatchPartyItem
            {
                IsWaitingRoom = true,
                IsPlaying = false,
                AutoStartWhenReady = true,
                MinReadyCount = minReadyCount
            };
        }
    }
}
