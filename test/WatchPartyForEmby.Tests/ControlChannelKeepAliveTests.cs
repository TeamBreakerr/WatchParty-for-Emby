using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Session;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class ControlChannelKeepAliveTests
    {
        [Fact]
        public async Task EveryLiveWebSocketIsWrittenTo()
        {
            // The bytes are the entire point: an idle socket is what the network path
            // reaps, and Emby's own Pong every 1800 seconds is far too rare to stop it.
            var phone = WebSocket(active: true);
            var browser = WebSocket(active: true);
            var keepAlive = new ControlChannelKeepAlive(() => "message-1");

            var report = await keepAlive.SendAsync(
                new[] { Session("ios", phone), Session("web", browser) },
                CancellationToken.None);

            Assert.Equal(
                new[] { ControlChannelKeepAlive.MessageName },
                phone.Messages);
            Assert.Equal(
                new[] { ControlChannelKeepAlive.MessageName },
                browser.Messages);
            Assert.Equal(new[] { "ios", "web" }, report.Delivered);
        }

        [Fact]
        public async Task FirebaseIsNeverUsedToKeepAControlChannelAlive()
        {
            // Emby's session manager is free to route a message to Firebase push. That
            // would put a notification on the participant's phone every two minutes,
            // so the keepalive goes to the WebSocket controller directly or nowhere.
            var firebase = SessionControllerProxy
                .Create<FirebaseSessionControllerProxy>(isSessionActive: true);
            var keepAlive = new ControlChannelKeepAlive();

            var report = await keepAlive.SendAsync(
                new[] { Session("ios", firebase) },
                CancellationToken.None);

            Assert.Empty(firebase.Messages);
            Assert.Empty(report.Delivered);
        }

        [Fact]
        public async Task AnInactiveWebSocketIsNotWrittenTo()
        {
            var closing = WebSocket(active: false);
            var keepAlive = new ControlChannelKeepAlive();

            var report = await keepAlive.SendAsync(
                new[] { Session("ios", closing) },
                CancellationToken.None);

            Assert.Empty(closing.Messages);
            Assert.Empty(report.Delivered);
        }

        [Fact]
        public async Task AWriteThatFailsCountsAsAChannelThatIsGone()
        {
            // A half-open socket accepts no writes. The exception is the diagnosis.
            var dead = WebSocket(active: true);
            dead.FailsToSend = true;
            var keepAlive = new ControlChannelKeepAlive();

            var report = await keepAlive.SendAsync(
                new[] { Session("ios", dead) },
                CancellationToken.None);

            Assert.Empty(report.Delivered);
        }

        [Fact]
        public async Task LosingAChannelIsReportedOnceRatherThanEveryTick()
        {
            // The log has to name the moment control was lost. Repeating it every two
            // minutes for the rest of the evening would bury exactly that.
            var phone = WebSocket(active: true);
            var keepAlive = new ControlChannelKeepAlive();
            var session = Session("ios", phone);

            await keepAlive.SendAsync(new[] { session }, CancellationToken.None);

            phone.IsSessionActive = false;
            var first = await keepAlive.SendAsync(
                new[] { session }, CancellationToken.None);
            var second = await keepAlive.SendAsync(
                new[] { session }, CancellationToken.None);

            Assert.Equal(new[] { "ios" }, first.Lost);
            Assert.Empty(second.Lost);
        }

        [Fact]
        public async Task AChannelThatComesBackIsReported()
        {
            var phone = WebSocket(active: false);
            var keepAlive = new ControlChannelKeepAlive();
            var session = Session("ios", phone);

            await keepAlive.SendAsync(new[] { session }, CancellationToken.None);

            phone.IsSessionActive = true;
            var report = await keepAlive.SendAsync(
                new[] { session }, CancellationToken.None);

            Assert.Equal(new[] { "ios" }, report.Restored);
            Assert.Equal(new[] { "ios" }, report.Delivered);
        }

        [Fact]
        public async Task ASessionThatLeavesEveryRoomIsForgotten()
        {
            // Otherwise the remembered set grows for the life of the server, and a
            // session that comes back unreachable would be announced as newly lost
            // on evidence gathered hours earlier.
            var phone = WebSocket(active: true);
            var keepAlive = new ControlChannelKeepAlive();

            await keepAlive.SendAsync(
                new[] { Session("ios", phone) }, CancellationToken.None);
            await keepAlive.SendAsync(
                Array.Empty<SessionInfo>(), CancellationToken.None);

            phone.IsSessionActive = false;
            var report = await keepAlive.SendAsync(
                new[] { Session("ios", phone) }, CancellationToken.None);

            Assert.Empty(report.Lost);
        }

        [Fact]
        public async Task ClearForgetsEveryChannel()
        {
            var phone = WebSocket(active: true);
            var keepAlive = new ControlChannelKeepAlive();

            await keepAlive.SendAsync(
                new[] { Session("ios", phone) }, CancellationToken.None);
            keepAlive.Clear();

            phone.IsSessionActive = false;
            var report = await keepAlive.SendAsync(
                new[] { Session("ios", phone) }, CancellationToken.None);

            Assert.Empty(report.Lost);
        }

        private static WebSocketSessionControllerProxy WebSocket(bool active)
        {
            return SessionControllerProxy
                .Create<WebSocketSessionControllerProxy>(isSessionActive: active);
        }

        private static SessionInfo Session(
            string id,
            SessionControllerProxy controller)
        {
            return new SessionInfo
            {
                Id = id,
                SessionControllers = new[] { controller.Controller }
            };
        }
    }
}
