using System.Linq;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class PartyLaunchTargetEligibilityTests
    {
        [Theory]
        [InlineData(true, true, true, true, "当前 Master Session")]
        [InlineData(false, false, true, true, "不符合房间加入条件")]
        [InlineData(false, true, false, true, "客户端未声明远程播放能力")]
        [InlineData(false, true, true, false, "在线，但控制连接未建立")]
        public void IneligibleSessionExplainsWhyItCannotLaunch(
            bool isMaster,
            bool canJoin,
            bool supportsRemoteControl,
            bool hasActiveWebSocket,
            string expectedMessage)
        {
            var result = PartyLaunchTargetEligibility.Decide(
                new PartyLaunchTargetFacts
                {
                    IsMaster = isMaster,
                    CanJoin = canJoin,
                    SupportsRemoteControl = supportsRemoteControl,
                    HasActiveWebSocket = hasActiveWebSocket,
                    InRoom = false
                });

            Assert.False(result.CanLaunch);
            Assert.Equal(expectedMessage, result.Message);
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
                    CanJoin = true,
                    SupportsRemoteControl = true,
                    HasActiveWebSocket = true,
                    InRoom = inRoom
                });

            Assert.True(result.CanLaunch);
            Assert.Equal(expectedMessage, result.Message);
        }

        [Fact]
        public void ProjectorOnlyMarksAnActiveControllableIosWebSocketAsLaunchable()
        {
            var activeWebSocket =
                SessionControllerProxy.Create<WebSocketSessionControllerProxy>(
                    isSessionActive: true);
            var inactiveWebSocket =
                SessionControllerProxy.Create<WebSocketSessionControllerProxy>(
                    isSessionActive: false);
            var targets = OfficialIosPartyLaunchTargetProjector.Project(
                new[]
                {
                    Session("ready", activeWebSocket.Controller),
                    Session("stale", inactiveWebSocket.Controller, "emby for ios")
                },
                masterSessionId: null,
                canJoin: _ => true,
                isInRoom: sessionId => sessionId == "ready");

            Assert.Equal(new[] { "ready", "stale" }, targets.Select(target => target.SessionId));
            Assert.True(targets[0].CanLaunch);
            Assert.True(targets[0].InRoom);
            Assert.False(targets[1].CanLaunch);
            Assert.Equal("在线，但控制连接未建立", targets[1].Message);
        }

        [Fact]
        public void LowercaseIosWithoutWebSocketIsDisplayedButRejectedOnSubmission()
        {
            var firebase = SessionControllerProxy.Create<FirebaseSessionControllerProxy>(
                isSessionActive: true);
            var session = Session("lowercase-ios", firebase.Controller, "emby for ios");
            var evaluation = OfficialIosPartyLaunchTargetProjector.BuildEvaluatedTarget(
                session,
                masterSessionId: null,
                canJoin: true,
                inRoom: false);

            Assert.Single(OfficialIosPartySessionDiscovery.Discover(new[] { session }));
            Assert.False(evaluation.CanLaunch);
            Assert.Equal("在线，但控制连接未建立", evaluation.Message);
            Assert.Empty(OfficialIosPartySessionDiscovery.SelectRequested(
                new[] { session },
                requestedSessionIds: new[] { session.Id },
                canReceiveLaunchCommand: candidate =>
                    OfficialIosPartyLaunchTargetProjector.BuildEvaluatedTarget(
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
                Capabilities = new ClientCapabilities
                {
                    SupportsMediaControl = true,
                    PlayableMediaTypes = new[] { "Video" }
                },
                SessionControllers = new[] { controller }
            };
        }
    }
}
