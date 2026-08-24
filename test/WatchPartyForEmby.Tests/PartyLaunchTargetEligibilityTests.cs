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
                isInRoom: session => session.Id == "ready");

            Assert.Equal(
                new[] { "ready", "stale" },
                targets.Select(target => target.SessionId));
            Assert.True(targets[0].CanLaunch);
            Assert.True(targets[0].InRoom);
            Assert.False(targets[1].CanLaunch);
            Assert.Equal("在线，但控制连接未建立", targets[1].Message);
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
        public void ProjectorRejectsAWebSessionSharedByMultipleActiveControllers()
        {
            var firstTab =
                SessionControllerProxy.Create<WebSocketSessionControllerProxy>(
                    isSessionActive: true);
            var secondTab =
                SessionControllerProxy.Create<WebSocketSessionControllerProxy>(
                    isSessionActive: true);
            var session = Session(
                "shared-web-session",
                firstTab.Controller,
                "master",
                "Emby Web",
                DateTime.UtcNow);
            session.SessionControllers = new[]
            {
                firstTab.Controller,
                secondTab.Controller
            };

            var target = PartyLaunchTargetProjector.Project(
                new[] { session },
                masterSessionId: session.Id,
                masterUserId: session.UserId,
                canJoin: _ => true,
                isInRoom: _ => false,
                nowUtc: DateTime.UtcNow).Single();

            Assert.False(target.CanLaunch);
            Assert.Equal(2, target.ActiveControllerCount);
            Assert.True(target.HasAmbiguousWebControllers);
            Assert.Equal(
                "检测到 2 个活动 Web 控制连接共享此 Session；" +
                "通常是同一浏览器打开了多个 Emby 标签页，请关闭多余标签页后重试",
                target.Message);
            Assert.Empty(PartySessionDiscovery.SelectRequested(
                new[] { session },
                DateTime.UtcNow,
                new[] { session.Id },
                candidate => PartyLaunchTargetProjector.BuildEvaluatedTarget(
                    candidate,
                    masterSessionId: session.Id,
                    masterUserId: session.UserId,
                    canJoin: true,
                    inRoom: false,
                    nowUtc: DateTime.UtcNow).CanLaunch));
        }

        [Fact]
        public void ProjectorDeterminesRoomPresenceFromTheLiveSession()
        {
            var controller =
                SessionControllerProxy.Create<WebSocketSessionControllerProxy>(
                    isSessionActive: true);
            var active = Session(
                "active-session",
                controller.Controller,
                "room-user",
                "Emby Web",
                DateTime.UtcNow);
            var idle = Session(
                "idle-session",
                controller.Controller,
                "idle-user",
                "Emby Web",
                DateTime.UtcNow);

            var targets = PartyLaunchTargetProjector.Project(
                new[] { active, idle },
                masterSessionId: null,
                masterUserId: null,
                canJoin: _ => true,
                isInRoom: session => session.UserId == "room-user",
                nowUtc: DateTime.UtcNow);

            Assert.True(targets.Single(target => target.SessionId == active.Id).InRoom);
            Assert.False(targets.Single(target => target.SessionId == idle.Id).InRoom);
        }

        [Fact]
        public void ProjectorRejectsVariantWebClientNamesWithMultipleControllers()
        {
            var firstController =
                SessionControllerProxy.Create<WebSocketSessionControllerProxy>(
                    isSessionActive: true);
            var secondController =
                SessionControllerProxy.Create<WebSocketSessionControllerProxy>(
                    isSessionActive: true);
            var session = Session(
                "shared-versioned-web-session",
                firstController.Controller,
                "master",
                "Emby Web 4.9",
                DateTime.UtcNow);
            session.SessionControllers = new[]
            {
                firstController.Controller,
                secondController.Controller
            };

            var target = PartyLaunchTargetProjector.BuildEvaluatedTarget(
                session,
                masterSessionId: session.Id,
                masterUserId: session.UserId,
                canJoin: true,
                inRoom: false,
                nowUtc: DateTime.UtcNow);

            Assert.False(target.CanLaunch);
            Assert.True(target.HasAmbiguousWebControllers);
        }

        [Fact]
        public void RecentlyActiveIosWithoutWebSocketRemainsVisibleButCannotBeSelected()
        {
            var firebase = SessionControllerProxy.Create<FirebaseSessionControllerProxy>(
                isSessionActive: true);
            var session = Session("lowercase-ios", firebase.Controller, "emby for ios");
            var evaluation = PartyLaunchTargetProjector.BuildEvaluatedTarget(
                session,
                masterSessionId: null,
                canJoin: true,
                inRoom: false);

            Assert.Single(PartySessionDiscovery.Discover(
                new[] { session },
                DateTime.UtcNow));
            Assert.False(evaluation.CanLaunch);
            Assert.Equal("在线，但控制连接未建立", evaluation.Message);
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
