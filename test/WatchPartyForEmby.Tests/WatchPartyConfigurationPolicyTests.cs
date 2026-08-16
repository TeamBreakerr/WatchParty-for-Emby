using System.Collections.Generic;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class WatchPartyConfigurationPolicyTests
    {
        [Fact]
        public void NullPartyDoesNotRequireNormalization()
        {
            Assert.False(WatchPartyConfigurationPolicy.Normalize(null));
        }

        [Fact]
        public void NullCollectionsAreInitialized()
        {
            var party = ValidParty();
            party.AllowedUserIds = null;
            party.EpisodeQueue = null;

            Assert.True(WatchPartyConfigurationPolicy.Normalize(party));
            Assert.NotNull(party.AllowedUserIds);
            Assert.Empty(party.AllowedUserIds);
            Assert.NotNull(party.EpisodeQueue);
            Assert.Empty(party.EpisodeQueue);
        }

        [Theory]
        [InlineData("HostOnly")]
        [InlineData("Master")]
        [InlineData("MasterOnly")]
        [InlineData("Disabled")]
        [InlineData(" host ")]
        public void HostPauseAliasesUseSafeHostOnlyBehavior(string pauseControl)
        {
            var party = ValidParty();
            party.PauseControl = pauseControl;

            Assert.True(WatchPartyConfigurationPolicy.Normalize(party));
            Assert.Equal("Host", party.PauseControl);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Nobody")]
        public void MissingOrInvalidPauseControlFallsBackToAnyone(string pauseControl)
        {
            var party = ValidParty();
            party.PauseControl = pauseControl;

            Assert.True(WatchPartyConfigurationPolicy.Normalize(party));
            Assert.Equal("Anyone", party.PauseControl);
        }

        [Theory]
        [InlineData("Anyone", "Anyone")]
        [InlineData("Vote", "Vote")]
        [InlineData("HOST", "Host")]
        [InlineData("vote", "Vote")]
        public void SupportedPauseControlsAreCanonicalized(string pauseControl, string expected)
        {
            var party = ValidParty();
            party.PauseControl = pauseControl;

            var changed = WatchPartyConfigurationPolicy.Normalize(party);

            Assert.Equal(expected, party.PauseControl);
            Assert.Equal(pauseControl != expected, changed);
        }

        [Fact]
        public void MissingNumericSettingsReceiveDefaults()
        {
            var party = ValidParty();
            party.MaxParticipants = 0;
            party.MinReadyCount = 0;
            party.SyncToleranceSeconds = 0;
            party.MaxBufferThresholdSeconds = 0;
            party.InactiveTimeoutMinutes = 0;

            Assert.True(WatchPartyConfigurationPolicy.Normalize(party));
            Assert.Equal(50, party.MaxParticipants);
            Assert.Equal(1, party.MinReadyCount);
            Assert.Equal(10, party.SyncToleranceSeconds);
            Assert.Equal(30, party.MaxBufferThresholdSeconds);
            Assert.Equal(15, party.InactiveTimeoutMinutes);
        }

        [Fact]
        public void NumericSettingsAreClampedToTheirLowerBounds()
        {
            var party = ValidParty();
            party.MaxParticipants = -10;
            party.MinReadyCount = -10;
            party.SyncToleranceSeconds = -10;
            party.MaxBufferThresholdSeconds = -10;
            party.InactiveTimeoutMinutes = -10;

            Assert.True(WatchPartyConfigurationPolicy.Normalize(party));
            Assert.Equal(2, party.MaxParticipants);
            Assert.Equal(1, party.MinReadyCount);
            Assert.Equal(1, party.SyncToleranceSeconds);
            Assert.Equal(10, party.MaxBufferThresholdSeconds);
            Assert.Equal(5, party.InactiveTimeoutMinutes);
        }

        [Fact]
        public void NumericSettingsAreClampedToTheirUpperBounds()
        {
            var party = ValidParty();
            party.MaxParticipants = 101;
            party.MinReadyCount = 101;
            party.SyncToleranceSeconds = 61;
            party.MaxBufferThresholdSeconds = 121;
            party.InactiveTimeoutMinutes = 121;

            Assert.True(WatchPartyConfigurationPolicy.Normalize(party));
            Assert.Equal(100, party.MaxParticipants);
            Assert.Equal(100, party.MinReadyCount);
            Assert.Equal(60, party.SyncToleranceSeconds);
            Assert.Equal(120, party.MaxBufferThresholdSeconds);
            Assert.Equal(120, party.InactiveTimeoutMinutes);
        }

        [Fact]
        public void MinimumReadyCountIsLimitedByNormalizedCapacity()
        {
            var party = ValidParty();
            party.MaxParticipants = 3;
            party.MinReadyCount = 20;

            Assert.True(WatchPartyConfigurationPolicy.Normalize(party));
            Assert.Equal(3, party.MaxParticipants);
            Assert.Equal(3, party.MinReadyCount);
        }

        [Fact]
        public void MissingMasterUsesExistingHostAndKeepsBothAligned()
        {
            var party = ValidParty();
            party.MasterUserId = null;
            party.HostUserId = "host-user";

            Assert.True(WatchPartyConfigurationPolicy.Normalize(party));
            Assert.Equal("host-user", party.MasterUserId);
            Assert.Equal("host-user", party.HostUserId);
        }

        [Fact]
        public void MasterTakesPrecedenceOverDifferentHost()
        {
            var party = ValidParty();
            party.MasterUserId = "master-user";
            party.HostUserId = "former-host";

            Assert.True(WatchPartyConfigurationPolicy.Normalize(party));
            Assert.Equal("master-user", party.HostUserId);
        }

        [Fact]
        public void RestrictedRoomAlwaysWhitelistsItsMaster()
        {
            var party = ValidParty();
            party.MasterUserId = "master-user";
            party.HostUserId = "master-user";
            party.AllowedUserIds.Add("viewer-user");

            Assert.True(WatchPartyConfigurationPolicy.Normalize(party));
            Assert.Equal(new[] { "viewer-user", "master-user" }, party.AllowedUserIds);
        }

        [Fact]
        public void ExistingMasterWhitelistEntryIsComparedCaseInsensitively()
        {
            var party = ValidParty();
            party.MasterUserId = "MASTER-USER";
            party.HostUserId = "MASTER-USER";
            party.AllowedUserIds.Add("master-user");

            Assert.False(WatchPartyConfigurationPolicy.Normalize(party));
            Assert.Single(party.AllowedUserIds);
        }

        [Fact]
        public void UnrestrictedRoomDoesNotCreateAWhitelist()
        {
            var party = ValidParty();
            party.MasterUserId = "master-user";
            party.HostUserId = "master-user";

            Assert.False(WatchPartyConfigurationPolicy.Normalize(party));
            Assert.Empty(party.AllowedUserIds);
        }

        [Fact]
        public void AutoStartIsDisabledWithoutAWaitingRoom()
        {
            var party = ValidParty();
            party.IsWaitingRoom = false;
            party.AutoStartWhenReady = true;

            Assert.True(WatchPartyConfigurationPolicy.Normalize(party));
            Assert.False(party.AutoStartWhenReady);
        }

        [Fact]
        public void SeriesQueueInvariantsAreRepaired()
        {
            var party = ValidParty();
            party.IsSeriesParty = true;
            party.CurrentEpisodeId = "s1e2";
            party.EpisodeQueue = new List<WatchPartyEpisode>
            {
                Episode("s2e1", 2, 1),
                Episode("s1e2", 1, 2),
                null,
                Episode("s1e1", 1, 1),
                Episode("S1E2", 1, 2)
            };

            Assert.True(WatchPartyConfigurationPolicy.Normalize(party));
            Assert.Equal(new[] { "s1e1", "s1e2", "s2e1" }, party.EpisodeQueue.ConvertAll(e => e.ItemId));
            Assert.Equal(1, party.CurrentEpisodeIndex);
            Assert.Equal("s1e2", party.ItemId);
            Assert.Equal("s1e2", party.CurrentEpisodeId);
        }

        [Fact]
        public void NormalizationIsIdempotentAfterRepairingAllInvalidState()
        {
            var party = new WatchPartyItem
            {
                AllowedUserIds = new List<string> { "viewer-user" },
                AutoStartWhenReady = true,
                EpisodeQueue = new List<WatchPartyEpisode>
                {
                    Episode("s1e2", 1, 2),
                    Episode("s1e1", 1, 1)
                },
                HostUserId = "host-user",
                InactiveTimeoutMinutes = 500,
                IsSeriesParty = true,
                IsWaitingRoom = false,
                MasterUserId = null,
                MaxBufferThresholdSeconds = 1,
                MaxParticipants = 500,
                MinReadyCount = 500,
                PauseControl = "Master",
                SyncToleranceSeconds = 500
            };

            Assert.True(WatchPartyConfigurationPolicy.Normalize(party));
            Assert.False(WatchPartyConfigurationPolicy.Normalize(party));
        }

        private static WatchPartyItem ValidParty()
        {
            return new WatchPartyItem
            {
                AutoStartWhenReady = true,
                HostUserId = "owner-user",
                IsWaitingRoom = true,
                MasterUserId = "owner-user",
                MaxBufferThresholdSeconds = 30,
                MaxParticipants = 50,
                MinReadyCount = 1,
                PauseControl = "Anyone",
                SyncToleranceSeconds = 10,
                InactiveTimeoutMinutes = 15
            };
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
