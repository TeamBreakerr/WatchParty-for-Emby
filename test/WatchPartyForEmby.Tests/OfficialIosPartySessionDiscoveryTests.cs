using System.Linq;
using MediaBrowser.Controller.Session;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class OfficialIosPartySessionDiscoveryTests
    {
        [Fact]
        public void SelectsEligibleIosSessionsThatHaveNotEnteredTheRoom()
        {
            var selected = OfficialIosPartySessionDiscovery.Select(
                new[]
                {
                    Session("master-web", "master", "Emby Web"),
                    Session("master-ios-viewer", "master", "Emby for iOS"),
                    Session("friend-ios", "friend", "Emby for iOS"),
                    Session("friend-vibhub", "friend", "VidHub")
                },
                "master-web",
                session => session.UserId != "blocked");

            Assert.Equal(
                new[] { "master-ios-viewer", "friend-ios" },
                selected.Select(session => session.Id).ToArray());
        }

        [Fact]
        public void ExcludesTheActualMasterSessionAndIneligibleUsers()
        {
            var selected = OfficialIosPartySessionDiscovery.Select(
                new[]
                {
                    Session("master-ios", "master", "Emby for iOS"),
                    Session("blocked-ios", "blocked", "Emby for iOS"),
                    Session("allowed-ios", "allowed", "emby for ios")
                },
                "master-ios",
                session => session.UserId == "allowed");

            Assert.Single(selected);
            Assert.Equal("allowed-ios", selected[0].Id);
        }

        [Fact]
        public void RequestedSelectionOnlyReturnsExplicitEligibleSessionIds()
        {
            var selected = OfficialIosPartySessionDiscovery.SelectRequested(
                new[]
                {
                    Session("master-ios", "master", "Emby for iOS"),
                    Session("first-ios", "first", "Emby for iOS"),
                    Session("second-ios", "second", "Emby for iOS"),
                    Session("web", "second", "Emby Web")
                },
                new[] { "master-ios", "first-ios", "second-ios", "second-ios", "web", "unknown" },
                session => session.Id == "second-ios");

            Assert.Equal(new[] { "second-ios" }, selected.Select(session => session.Id));
        }

        [Fact]
        public void EmptyRequestedSelectionNeverFallsBackToAllOnlineSessions()
        {
            var selected = OfficialIosPartySessionDiscovery.SelectRequested(
                new[] { Session("friend-ios", "friend", "Emby for iOS") },
                requestedSessionIds: System.Array.Empty<string>(),
                canReceiveLaunchCommand: _ => true);

            Assert.Empty(selected);
        }

        [Fact]
        public void RequestedSessionWithoutAControlConnectionIsRejectedBeforeRegistration()
        {
            var selected = OfficialIosPartySessionDiscovery.SelectRequested(
                new[] { Session("stale-ios", "friend", "emby for ios") },
                requestedSessionIds: new[] { "stale-ios" },
                canReceiveLaunchCommand: _ => false);

            Assert.Empty(selected);
        }

        private static SessionInfo Session(string id, string userId, string client)
        {
            return new SessionInfo
            {
                Id = id,
                UserId = userId,
                Client = client
            };
        }
    }
}
