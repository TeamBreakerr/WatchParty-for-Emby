using System;
using System.Linq;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class PartyLaunchTargetEligibilityTests
    {
        [Theory]
        [InlineData(false, false, true, "不符合房间加入条件")]
        [InlineData(false, true, false, "客户端未声明远程播放能力")]
        public void IneligibleSessionExplainsWhyItCannotLaunch(
            bool isMaster,
            bool canJoin,
            bool supportsRemoteControl,
            string expectedMessage)
        {
            var result = PartyLaunchTargetEligibility.Decide(
                new PartyLaunchTargetFacts
                {
                    IsMaster = isMaster,
                    IsOnline = true,
                    CanJoin = canJoin,
                    SupportsRemoteControl = supportsRemoteControl,
                    InRoom = false
                });

            Assert.False(result.CanLaunch);
            Assert.Equal(expectedMessage, result.Message);
        }

        [Fact]
        public void MasterWebSessionCanBeLaunchedBeforeItStartsPlayback()
        {
            var result = PartyLaunchTargetEligibility.Decide(
                new PartyLaunchTargetFacts
                {
                    IsMaster = true,
                    IsOnline = true,
                    CanJoin = true,
                    SupportsRemoteControl = true,
                    InRoom = false
                });

            Assert.True(result.CanLaunch);
            Assert.Equal("在线 · 可开播", result.Message);
        }

        [Theory]
        [InlineData(true, "在线 · 已在房间")]
        [InlineData(false, "在线 · 可拉入房间")]
        public void EligibleSessionReportsItsRoomMembership(
            bool inRoom,
            string expectedMessage)
        {
            var result = PartyLaunchTargetEligibility.Decide(
                new PartyLaunchTargetFacts
                {
                    IsMaster = false,
                    IsOnline = true,
                    CanJoin = true,
                    SupportsRemoteControl = true,
                    InRoom = inRoom
                });

            Assert.True(result.CanLaunch);
            Assert.Equal(expectedMessage, result.Message);
        }

        [Fact]
        public void ProjectorOnlyListsAnActiveControllableIosWebSocket()
        {
            var activeWebSocket =
                SessionControllerProxy.Create<WebSocketSessionControllerProxy>(
                    isSessionActive: true);
            var inactiveWebSocket =
                SessionControllerProxy.Create<WebSocketSessionControllerProxy>(
                    isSessionActive: false);
            var targets = PartyLaunchTargetProjector.Project(
                new[]
                {
                    Session("ready", activeWebSocket.Controller),
                    Session("stale", inactiveWebSocket.Controller, "emby for ios")
                },
                masterSessionId: null,
                canJoin: _ => true,
                isInRoom: sessionId => sessionId == "ready");

            Assert.Equal(new[] { "ready" }, targets.Select(target => target.SessionId));
            Assert.True(targets[0].CanLaunch);
            Assert.True(targets[0].InRoom);
        }

        [Fact]
        public void ProjectorIncludesIdleOpenWebAndMasterSessions()
        {
            var nowUtc = new DateTime(2026, 8, 24, 8, 0, 0, DateTimeKind.Utc);
            var masterController =
                SessionControllerProxy.Create<WebSocketSessionControllerProxy>(
                    isSessionActive: true);
            var friendController =
                SessionControllerProxy.Create<WebSocketSessionControllerProxy>(
                    isSessionActive: true);
            var targets = PartyLaunchTargetProjector.Project(
                new[]
                {
                    Session(
                        "master-web",
                        masterController.Controller,
                        "master",
                        "Emby Web",
                        nowUtc.AddHours(-1)),
                    Session(
                        "friend-web",
                        friendController.Controller,
                        "friend",
                        "Emby Web",
                        nowUtc.AddHours(-1))
                },
                masterSessionId: null,
                masterUserId: "master",
                canJoin: _ => true,
                isInRoom: _ => false,
                nowUtc: nowUtc);

            Assert.Equal(
                new[] { "master-web", "friend-web" },
                targets.Select(target => target.SessionId));
            Assert.All(targets, target => Assert.True(target.CanLaunch));
            Assert.True(targets[0].IsMaster);
        }

        [Fact]
        public void LowercaseIosWithoutWebSocketIsOfflineAndCannotBeSelected()
        {
            var firebase = SessionControllerProxy.Create<FirebaseSessionControllerProxy>(
                isSessionActive: true);
            var session = Session("lowercase-ios", firebase.Controller, "emby for ios");
            var evaluation = PartyLaunchTargetProjector.BuildEvaluatedTarget(
                session,
                masterSessionId: null,
                canJoin: true,
                inRoom: false);

            Assert.Empty(PartySessionDiscovery.Discover(
                new[] { session },
                DateTime.UtcNow));
            Assert.False(evaluation.CanLaunch);
            Assert.Equal("客户端已离线", evaluation.Message);
            Assert.Empty(PartySessionDiscovery.SelectRequested(
                new[] { session },
                DateTime.UtcNow,
                requestedSessionIds: new[] { session.Id },
                canReceiveLaunchCommand: candidate =>
                    PartyLaunchTargetProjector.BuildEvaluatedTarget(
                        candidate,
                        masterSessionId: null,
                        canJoin: true,
                        inRoom: false).CanLaunch));
        }

        private static SessionInfo Session(
            string sessionId,
            ISessionController controller,
            string client = "Emby for iOS")
        {
            return new SessionInfo
            {
                Id = sessionId,
                UserId = sessionId,
                UserName = sessionId,
                DeviceName = "iPhone",
                Client = client,
                LastActivityDate = DateTimeOffset.UtcNow,
                Capabilities = new ClientCapabilities
                {
                    SupportsMediaControl = true,
                    PlayableMediaTypes = new[] { "Video" }
                },
                SessionControllers = new[] { controller }
            };
        }

        private static SessionInfo Session(
            string sessionId,
            ISessionController controller,
            string userId,
            string client,
            DateTime nowUtc)
        {
            var session = Session(sessionId, controller, client);
            session.UserId = userId;
            session.LastActivityDate = new DateTimeOffset(nowUtc);
            return session;
        }
    }
}
