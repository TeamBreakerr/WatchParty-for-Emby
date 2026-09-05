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
    ///
    /// This class used to record that Emby sends protocol-level WebSocket pings, and
    /// concluded from that no keepalive of our own was warranted. Reading the wire
    /// disproved it: what Emby sends is one unsolicited Pong every 1800 seconds, and
    /// a Pong obliges the client to answer nothing. The socket therefore goes idle
    /// for half-hour stretches and dies unnoticed, which is what
    /// <see cref="ControlChannelKeepAlive"/> now exists to prevent.
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
            return SelectActiveWebSocketControllers(controllers).Count;
        }

        /// <summary>
        /// The live WebSocket controllers themselves, for callers that need to write
        /// to the socket rather than merely know that one exists.
        /// </summary>
        public static IReadOnlyList<ISessionController> SelectActiveWebSocketControllers(
            IEnumerable<ISessionController> controllers)
        {
            return controllers?
                .Where(controller => controller != null
                    && controller.IsSessionActive
                    && IsWebSocketController(controller))
                .ToList()
                ?? (IReadOnlyList<ISessionController>)Array.Empty<ISessionController>();
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
