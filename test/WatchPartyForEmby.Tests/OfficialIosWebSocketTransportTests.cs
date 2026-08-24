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
        public async Task KeepAliveOnlyUsesTheActiveWebSocketController()
        {
            var webSocket = SessionControllerProxy.Create<WebSocketSessionControllerProxy>(
                isSessionActive: true);
            var firebase = SessionControllerProxy.Create<FirebaseSessionControllerProxy>(
                isSessionActive: true);
            var transport = new OfficialIosWebSocketTransport(TimeSpan.FromSeconds(10));

            var sent = await transport.TrySendKeepAliveAsync(
                "ios-session",
                "Emby for iOS",
                new[] { firebase.Controller, webSocket.Controller },
                new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc),
                CancellationToken.None);

            Assert.True(sent);
            Assert.Equal(new[] { "KeepAlive" }, webSocket.Messages);
            Assert.Empty(firebase.Messages);
        }

        [Fact]
        public async Task KeepAliveSkipsInactiveWebSocketAndFirebaseControllers()
        {
            var inactiveWebSocket =
                SessionControllerProxy.Create<WebSocketSessionControllerProxy>(
                    isSessionActive: false);
            var firebase = SessionControllerProxy.Create<FirebaseSessionControllerProxy>(
                isSessionActive: true);
            var transport = new OfficialIosWebSocketTransport(TimeSpan.FromSeconds(10));

            var sent = await transport.TrySendKeepAliveAsync(
                "ios-session",
                "Emby for iOS",
                new[] { inactiveWebSocket.Controller, firebase.Controller },
                new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc),
                CancellationToken.None);

            Assert.False(sent);
            Assert.Empty(inactiveWebSocket.Messages);
            Assert.Empty(firebase.Messages);
        }

        [Fact]
        public async Task KeepAliveIsLimitedToOnceEveryTenSecondsPerSession()
        {
            var webSocket = SessionControllerProxy.Create<WebSocketSessionControllerProxy>(
                isSessionActive: true);
            var transport = new OfficialIosWebSocketTransport(TimeSpan.FromSeconds(10));
            var firstAttemptUtc = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

            var firstSent = await transport.TrySendKeepAliveAsync(
                "ios-session",
                "Emby for iOS",
                new[] { webSocket.Controller },
                firstAttemptUtc,
                CancellationToken.None);
            var earlyRetrySent = await transport.TrySendKeepAliveAsync(
                "ios-session",
                "Emby for iOS",
                new[] { webSocket.Controller },
                firstAttemptUtc.AddSeconds(9),
                CancellationToken.None);
            var dueRetrySent = await transport.TrySendKeepAliveAsync(
                "ios-session",
                "Emby for iOS",
                new[] { webSocket.Controller },
                firstAttemptUtc.AddSeconds(10),
                CancellationToken.None);

            Assert.True(firstSent);
            Assert.False(earlyRetrySent);
            Assert.True(dueRetrySent);
            Assert.Equal(new[] { "KeepAlive", "KeepAlive" }, webSocket.Messages);
        }

        [Fact]
        public async Task FailedKeepAliveCanBeRetriedBeforeTheNextInterval()
        {
            var webSocket = SessionControllerProxy.Create<WebSocketSessionControllerProxy>(
                isSessionActive: true);
            webSocket.FailuresRemaining = 1;
            var transport = new OfficialIosWebSocketTransport(TimeSpan.FromSeconds(10));
            var firstAttemptUtc = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                transport.TrySendKeepAliveAsync(
                    "ios-session",
                    "Emby for iOS",
                    new[] { webSocket.Controller },
                    firstAttemptUtc,
                    CancellationToken.None));
            var retrySent = await transport.TrySendKeepAliveAsync(
                "ios-session",
                "Emby for iOS",
                new[] { webSocket.Controller },
                firstAttemptUtc.AddSeconds(1),
                CancellationToken.None);

            Assert.True(retrySent);
            Assert.Equal(new[] { "KeepAlive" }, webSocket.Messages);
        }

        [Fact]
        public void OfficialIosWithoutAnActiveWebSocketCannotReceivePlaybackCommands()
        {
            var inactiveWebSocket =
                SessionControllerProxy.Create<WebSocketSessionControllerProxy>(
                    isSessionActive: false);
            var firebase = SessionControllerProxy.Create<FirebaseSessionControllerProxy>(
                isSessionActive: true);
            var transport = new OfficialIosWebSocketTransport(TimeSpan.FromSeconds(10));

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
            var transport = new OfficialIosWebSocketTransport(TimeSpan.FromSeconds(10));

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
                TimeSpan.FromSeconds(10),
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
                TimeSpan.FromSeconds(10),
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
            var transport = new OfficialIosWebSocketTransport(TimeSpan.FromSeconds(10));

            var canDispatch = transport.CanDispatchPlaybackCommand(
                "Emby Web",
                Array.Empty<ISessionController>());

            Assert.True(canDispatch);
        }

        [Fact]
        public async Task ClearingASessionRemovesItsKeepAliveLimit()
        {
            var webSocket = SessionControllerProxy.Create<WebSocketSessionControllerProxy>(
                isSessionActive: true);
            var transport = new OfficialIosWebSocketTransport(TimeSpan.FromSeconds(10));
            var nowUtc = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
            await transport.TrySendKeepAliveAsync(
                "ios-session",
                "Emby for iOS",
                new[] { webSocket.Controller },
                nowUtc,
                CancellationToken.None);

            transport.ClearSession("ios-session");
            var sentAfterClear = await transport.TrySendKeepAliveAsync(
                "ios-session",
                "Emby for iOS",
                new[] { webSocket.Controller },
                nowUtc.AddSeconds(1),
                CancellationToken.None);

            Assert.True(sentAfterClear);
            Assert.Equal(2, webSocket.Messages.Count);
        }

        [Fact]
        public async Task ClearingTheTransportRemovesEveryKeepAliveLimit()
        {
            var webSocket = SessionControllerProxy.Create<WebSocketSessionControllerProxy>(
                isSessionActive: true);
            var transport = new OfficialIosWebSocketTransport(TimeSpan.FromSeconds(10));
            var nowUtc = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
            await transport.TrySendKeepAliveAsync(
                "ios-session-a",
                "Emby for iOS",
                new[] { webSocket.Controller },
                nowUtc,
                CancellationToken.None);
            await transport.TrySendKeepAliveAsync(
                "ios-session-b",
                "Emby for iOS",
                new[] { webSocket.Controller },
                nowUtc,
                CancellationToken.None);

            transport.Clear();

            Assert.True(await transport.TrySendKeepAliveAsync(
                "ios-session-a",
                "Emby for iOS",
                new[] { webSocket.Controller },
                nowUtc.AddSeconds(1),
                CancellationToken.None));
            Assert.True(await transport.TrySendKeepAliveAsync(
                "ios-session-b",
                "Emby for iOS",
                new[] { webSocket.Controller },
                nowUtc.AddSeconds(1),
                CancellationToken.None));
        }
    }

    public abstract class SessionControllerProxy : DispatchProxy
    {
        public bool IsSessionActive { get; set; }

        public int FailuresRemaining { get; set; }

        public List<string> Messages { get; } = new List<string>();

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
                    if (FailuresRemaining > 0)
                    {
                        FailuresRemaining--;
                        return Task.FromException(
                            new InvalidOperationException("simulated WebSocket failure"));
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
