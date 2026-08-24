using System;
using System.Linq;
using MediaBrowser.Controller.Session;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class PartySessionDiscoveryTests
    {
        private static readonly DateTime NowUtc =
            new DateTime(2026, 8, 24, 8, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void DiscoverIncludesEveryRecentlyActiveClientType()
        {
            var iosWebSocket =
                SessionControllerProxy.Create<WebSocketSessionControllerProxy>(
                    isSessionActive: true);
            var sessions = new[]
            {
                Session("master-web", "master", "Emby Web", NowUtc.AddSeconds(-15)),
                Session(
                    "friend-ios",
                    "friend",
                    "Emby for iOS",
                    NowUtc.AddSeconds(-20),
                    iosWebSocket.Controller),
                Session("tv", "viewer", "Emby Theater", NowUtc.AddSeconds(-25)),
                Session("stale-web", "old", "Emby Web", NowUtc.AddMinutes(-4)),
                Session("killed-ios", "old", "Emby for iOS", NowUtc.AddSeconds(-10))
            };

            var discovered = PartySessionDiscovery.Discover(sessions, NowUtc);

            Assert.Equal(
                new[] { "master-web", "friend-ios", "tv" },
                discovered.Select(session => session.Id));
        }

        [Fact]
        public void RequestedSelectionNeverFallsBackToUnselectedOrOfflineSessions()
        {
            var sessions = new[]
            {
                Session("selected-web", "master", "Emby Web", NowUtc),
                Session("other-web", "friend", "Emby Web", NowUtc),
                Session("stale-web", "old", "Emby Web", NowUtc.AddMinutes(-5))
            };

            var selected = PartySessionDiscovery.SelectRequested(
                sessions,
                NowUtc,
                new[] { "selected-web", "stale-web", "unknown" },
                _ => true);

            Assert.Equal(new[] { "selected-web" }, selected.Select(session => session.Id));
        }

        private static SessionInfo Session(
            string id,
            string userId,
            string client,
            DateTime lastActivityUtc,
            ISessionController controller = null)
        {
            return new SessionInfo
            {
                Id = id,
                UserId = userId,
                UserName = userId,
                DeviceName = client + " device",
                Client = client,
                LastActivityDate = new DateTimeOffset(lastActivityUtc),
                SessionControllers = controller == null
                    ? Array.Empty<ISessionController>()
                    : new[] { controller }
            };
        }
    }
}
