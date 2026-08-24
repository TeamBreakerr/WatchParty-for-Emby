using System;
using MediaBrowser.Controller.Session;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class PartySessionLivenessPolicyTests
    {
        private static readonly DateTime NowUtc =
            new DateTime(2026, 8, 24, 8, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void RecentlyActiveWebSessionIsOnlineEvenWhenItIsNotPlaying()
        {
            var session = Session(
                "web-session",
                "Emby Web",
                NowUtc.AddSeconds(-30));

            Assert.True(PartySessionLivenessPolicy.IsOnline(session, NowUtc));
        }

        [Fact]
        public void IdleOpenWebSessionRemainsOnlineBeyondTheActivityFallbackWindow()
        {
            var controller = SessionControllerProxy.Create<WebSocketSessionControllerProxy>(
                isSessionActive: true);
            var session = Session(
                "idle-web-session",
                "Emby Web",
                NowUtc.AddHours(-1),
                controller.Controller);

            Assert.True(PartySessionLivenessPolicy.IsOnline(session, NowUtc));
        }

        [Fact]
        public void RetainedWebSessionWithoutAControllerExpires()
        {
            var session = Session(
                "closed-web-session",
                "Emby Web",
                NowUtc.AddMinutes(-4));

            Assert.False(PartySessionLivenessPolicy.IsOnline(session, NowUtc));
        }

        [Fact]
        public void RecentlyActiveWebSessionWithOnlyAnInactiveControllerIsOffline()
        {
            var controller = SessionControllerProxy.Create<WebSocketSessionControllerProxy>(
                isSessionActive: false);
            var session = Session(
                "closed-web-session",
                "Emby Web",
                NowUtc.AddSeconds(-5),
                controller.Controller);

            Assert.False(PartySessionLivenessPolicy.IsOnline(session, NowUtc));
        }

        [Fact]
        public void StaleIosSessionAfterBackgroundTerminationIsOffline()
        {
            var session = Session(
                "ios-session",
                "Emby for iOS",
                NowUtc.AddMinutes(-4));

            Assert.False(PartySessionLivenessPolicy.IsOnline(session, NowUtc));
        }

        [Fact]
        public void RecentlyActiveIosSessionRemainsVisibleWhenItsWebSocketIsTemporarilyGone()
        {
            var session = Session(
                "ios-session",
                "Emby for iOS",
                NowUtc.AddSeconds(-20));

            Assert.True(PartySessionLivenessPolicy.IsOnline(session, NowUtc));
        }

        [Fact]
        public void AcceptedPartyProgressKeepsIosParticipantOnlineWithoutControl()
        {
            var session = Session(
                "participant-ios-session",
                "Emby for iOS",
                NowUtc.AddMinutes(-4));

            Assert.True(PartySessionLivenessPolicy.IsParticipantOnline(
                session,
                NowUtc.AddSeconds(-10),
                NowUtc));
            Assert.False(PartySessionLivenessPolicy.IsParticipantOnline(
                session,
                NowUtc.AddSeconds(-31),
                NowUtc));
        }

        [Fact]
        public void RecentlyActiveIosSessionWithALiveWebSocketIsOnline()
        {
            var controller = SessionControllerProxy.Create<WebSocketSessionControllerProxy>(
                isSessionActive: true);
            var session = Session(
                "ios-session",
                "Emby for iOS",
                NowUtc.AddSeconds(-30),
                controller.Controller);

            Assert.True(PartySessionLivenessPolicy.IsOnline(session, NowUtc));
        }

        [Fact]
        public void IdleIosSessionWithALiveWebSocketRemainsOnline()
        {
            var controller = SessionControllerProxy.Create<WebSocketSessionControllerProxy>(
                isSessionActive: true);
            var session = Session(
                "idle-ios-session",
                "Emby for iOS",
                NowUtc.AddHours(-1),
                controller.Controller);

            Assert.True(PartySessionLivenessPolicy.IsOnline(session, NowUtc));
        }

        [Fact]
        public void NewSessionWithoutATimestampUsesItsActiveControllerHandshake()
        {
            var controller = SessionControllerProxy.Create<WebSocketSessionControllerProxy>(
                isSessionActive: true);
            var session = Session(
                "new-web-session",
                "Emby Web",
                DateTime.MinValue,
                controller.Controller);

            Assert.True(PartySessionLivenessPolicy.IsOnline(session, NowUtc));
        }

        private static SessionInfo Session(
            string id,
            string client,
            DateTime lastActivityUtc,
            ISessionController controller = null)
        {
            return new SessionInfo
            {
                Id = id,
                UserId = id,
                Client = client,
                LastActivityDate = lastActivityUtc == DateTime.MinValue
                    ? default
                    : new DateTimeOffset(lastActivityUtc),
                SessionControllers = controller == null
                    ? Array.Empty<ISessionController>()
                    : new[] { controller }
            };
        }
    }
}
