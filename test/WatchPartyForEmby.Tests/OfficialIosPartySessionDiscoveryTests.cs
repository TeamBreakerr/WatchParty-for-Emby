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
