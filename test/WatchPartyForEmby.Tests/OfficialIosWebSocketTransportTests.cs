using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Session;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class OfficialIosWebSocketTransportTests
    {
        [Fact]
        public void OfficialIosWithoutAnActiveWebSocketCannotReceivePlaybackCommands()
        {
            var inactiveWebSocket =
                SessionControllerProxy.Create<WebSocketSessionControllerProxy>(
                    isSessionActive: false);
            var firebase = SessionControllerProxy.Create<FirebaseSessionControllerProxy>(
                isSessionActive: true);
            var transport = new OfficialIosWebSocketTransport();

            var canDispatch = transport.CanDispatchPlaybackCommand(
                "Emby for iOS",
                new[] { inactiveWebSocket.Controller, firebase.Controller });

            Assert.False(canDispatch);
        }

        [Fact]
        public void LowercaseOfficialIosCannotBypassWebSocketRequirement()
        {
            var firebase = SessionControllerProxy.Create<FirebaseSessionControllerProxy>(
                isSessionActive: true);
            var transport = new OfficialIosWebSocketTransport();

            Assert.True(OfficialIosWebSocketTransport.IsOfficialIosClient(
                "emby for ios"));
            Assert.False(transport.CanDispatchPlaybackCommand(
                "emby for ios",
                new[] { firebase.Controller }));
        }

        [Fact]
        public async Task PlaybackCommandWaitsForAReconnectingIosWebSocket()
        {
            var firebase = SessionControllerProxy.Create<FirebaseSessionControllerProxy>(
                isSessionActive: true);
            var webSocket = SessionControllerProxy.Create<WebSocketSessionControllerProxy>(
                isSessionActive: true);
            IEnumerable<ISessionController> controllers = new[] { firebase.Controller };
            var waits = 0;
            var transport = new OfficialIosWebSocketTransport(
                (_, __) =>
                {
                    waits++;
                    controllers = new[] { firebase.Controller, webSocket.Controller };
                    return Task.CompletedTask;
                });

            var available = await transport.WaitForPlaybackCommandTransportAsync(
                "Emby for iOS",
                () => controllers,
                TimeSpan.FromSeconds(4),
                TimeSpan.FromMilliseconds(250),
                CancellationToken.None);

            Assert.True(available);
            Assert.Equal(1, waits);
            Assert.Empty(firebase.Messages);
        }

        [Fact]
        public async Task PlaybackCommandWaitTimesOutWithoutFallingBackToFirebase()
        {
            var firebase = SessionControllerProxy.Create<FirebaseSessionControllerProxy>(
                isSessionActive: true);
            var waits = 0;
            var transport = new OfficialIosWebSocketTransport(
                (_, __) =>
                {
                    waits++;
                    return Task.CompletedTask;
                });

            var available = await transport.WaitForPlaybackCommandTransportAsync(
                "Emby for iOS",
                () => new[] { firebase.Controller },
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromMilliseconds(250),
                CancellationToken.None);

            Assert.False(available);
            Assert.Equal(2, waits);
            Assert.Empty(firebase.Messages);
        }

        [Fact]
        public void NonIosPlaybackCommandsKeepTheirExistingTransportBehavior()
        {
            var transport = new OfficialIosWebSocketTransport();

            var canDispatch = transport.CanDispatchPlaybackCommand(
                "Emby Web",
                Array.Empty<ISessionController>());

            Assert.True(canDispatch);
        }

    }

    public abstract class SessionControllerProxy : DispatchProxy
    {
        public bool IsSessionActive { get; set; }

        public List<string> Messages { get; } = new List<string>();

        /// <summary>Makes the next write fail the way a dead socket does.</summary>
        public bool FailsToSend { get; set; }

        public ISessionController Controller { get; private set; }

        public static TProxy Create<TProxy>(bool isSessionActive)
            where TProxy : SessionControllerProxy
        {
            var controller = DispatchProxy.Create<ISessionController, TProxy>();
            var proxy = (TProxy)(object)controller;
            proxy.Controller = controller;
            proxy.IsSessionActive = isSessionActive;
            return proxy;
        }

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            switch (targetMethod.Name)
            {
                case "get_IsSessionActive":
                    return IsSessionActive;
                case "get_SupportsMediaControl":
                    return true;
                case "get_Priority":
                    return 0;
                case "SupportsMessage":
                    return true;
                case "SendMessage":
                    if (FailsToSend)
                    {
                        throw new InvalidOperationException("socket is gone");
                    }
                    Messages.Add(args[0].ToString());
                    return Task.CompletedTask;
                default:
                    throw new NotSupportedException(targetMethod.Name);
            }
        }
    }

    public class WebSocketSessionControllerProxy : SessionControllerProxy
    {
    }

    public class FirebaseSessionControllerProxy : SessionControllerProxy
    {
    }
}
