using System;
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
    /// Emby itself sends protocol-level WebSocket pings; this class deliberately does
    /// not inject an application-level KeepAlive message, which is not part of the
    /// official iOS command protocol.
    /// </summary>
    public sealed class OfficialIosWebSocketTransport
    {
        private const string OfficialIosClient = "Emby for iOS";
        private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

        public OfficialIosWebSocketTransport(
            Func<TimeSpan, CancellationToken, Task> delayAsync = null)
        {
            _delayAsync = delayAsync ?? Task.Delay;
        }

        public bool CanDispatchPlaybackCommand(
            string client,
            IEnumerable<ISessionController> controllers)
        {
            if (!IsOfficialIosClient(client))
            {
                return true;
            }

            return HasActiveWebSocketController(controllers);
        }

        public async Task<bool> WaitForPlaybackCommandTransportAsync(
            string client,
            Func<IEnumerable<ISessionController>> controllerProvider,
            TimeSpan timeout,
            TimeSpan pollInterval,
            CancellationToken cancellationToken)
        {
            if (!IsOfficialIosClient(client))
            {
                return true;
            }
            if (controllerProvider == null)
            {
                throw new ArgumentNullException(nameof(controllerProvider));
            }
            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }
            if (pollInterval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(pollInterval));
            }

            if (HasActiveWebSocketController(controllerProvider()))
            {
                return true;
            }

            var elapsed = TimeSpan.Zero;
            while (elapsed < timeout)
            {
                var delay = timeout - elapsed < pollInterval
                    ? timeout - elapsed
                    : pollInterval;
                await _delayAsync(delay, cancellationToken).ConfigureAwait(false);
                elapsed += delay;
                if (HasActiveWebSocketController(controllerProvider()))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool IsOfficialIosClient(string client)
        {
            return string.Equals(
                client,
                OfficialIosClient,
                StringComparison.OrdinalIgnoreCase);
        }

        public static bool HasActiveWebSocketController(
            IEnumerable<ISessionController> controllers)
        {
            return CountActiveWebSocketControllers(controllers) > 0;
        }

        public static int CountActiveWebSocketControllers(
            IEnumerable<ISessionController> controllers)
        {
            return controllers?.Count(controller => controller != null
                && controller.IsSessionActive
                && IsWebSocketController(controller)) ?? 0;
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
