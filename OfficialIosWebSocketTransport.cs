using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Session;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Keeps official iOS command traffic on its active WebSocket controller instead
    /// of allowing Emby's session manager to fall back to Firebase push delivery.
    /// </summary>
    public sealed class OfficialIosWebSocketTransport
    {
        private const string OfficialIosClient = "Emby for iOS";
        private readonly TimeSpan _keepAliveInterval;
        private readonly ConcurrentDictionary<string, DateTime> _lastKeepAliveUtc =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);

        public OfficialIosWebSocketTransport(TimeSpan keepAliveInterval)
        {
            if (keepAliveInterval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(keepAliveInterval));
            }

            _keepAliveInterval = keepAliveInterval;
        }

        public async Task<bool> TrySendKeepAliveAsync(
            string sessionId,
            string client,
            IEnumerable<ISessionController> controllers,
            DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(sessionId)
                || !string.Equals(client, OfficialIosClient, StringComparison.Ordinal))
            {
                return false;
            }

            var webSocket = controllers?
                .Where(controller => controller != null
                    && controller.IsSessionActive
                    && IsWebSocketController(controller))
                .OrderByDescending(controller => controller.Priority)
                .FirstOrDefault();
            if (webSocket == null)
            {
                return false;
            }

            if (_lastKeepAliveUtc.TryGetValue(sessionId, out var lastKeepAliveUtc)
                && nowUtc - lastKeepAliveUtc < _keepAliveInterval)
            {
                return false;
            }

            _lastKeepAliveUtc[sessionId] = nowUtc;

            try
            {
                await webSocket.SendMessage<object>(
                    "KeepAlive".AsMemory(),
                    Guid.NewGuid().ToString("N"),
                    null,
                    cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch
            {
                if (_lastKeepAliveUtc.TryGetValue(sessionId, out var reservedAtUtc)
                    && reservedAtUtc == nowUtc)
                {
                    _lastKeepAliveUtc.TryRemove(sessionId, out _);
                }
                throw;
            }
        }

        public bool CanDispatchPlaybackCommand(
            string client,
            IEnumerable<ISessionController> controllers)
        {
            if (!string.Equals(client, OfficialIosClient, StringComparison.Ordinal))
            {
                return true;
            }

            return controllers != null
                && controllers.Any(controller => controller != null
                    && controller.IsSessionActive
                    && IsWebSocketController(controller));
        }

        public void ClearSession(string sessionId)
        {
            if (!string.IsNullOrEmpty(sessionId))
            {
                _lastKeepAliveUtc.TryRemove(sessionId, out _);
            }
        }

        public void Clear()
        {
            _lastKeepAliveUtc.Clear();
        }

        private static bool IsWebSocketController(ISessionController controller)
        {
            for (var type = controller.GetType(); type != null; type = type.BaseType)
            {
                if (type.Name.IndexOf("WebSocket", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
