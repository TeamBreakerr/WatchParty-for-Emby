using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Session;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Keeps a participant's command WebSocket from being aged out by the network
    /// path between the server and the client.
    ///
    /// Emby's own keepalive was measured on the wire: one unsolicited Pong every
    /// 1800 seconds, and a Pong requires no reply. So the socket carries no client
    /// to server traffic at all, and one server to client frame every half hour. A
    /// router or carrier NAT that reaps an idle mapping sooner than that leaves the
    /// connection half open - the next Pong is never acknowledged, Linux retransmits
    /// it for tcp_retries2 (about 15 minutes on this server) and only then reports
    /// the socket dead. The client has sent nothing throughout, so it never learns
    /// the path is gone, never raises a close, and never reconnects. That is exactly
    /// how remote control disappears mid-film while HTTP progress reporting, which
    /// dials out on its own connections, carries on untouched.
    ///
    /// Writing a frame every couple of minutes keeps the mapping warm. It is sent
    /// straight to the WebSocket controller rather than through the session manager,
    /// which would be free to reroute it to Firebase push and put a notification on
    /// the participant's phone, and it carries a message type no client dispatches
    /// on, so it cannot disturb playback.
    /// </summary>
    public sealed class ControlChannelKeepAlive
    {
        /// <summary>
        /// Deliberately not one of Emby's own message types. A client that knows the
        /// type would do work for it; one that does not simply drops it, which is all
        /// this needs - the bytes on the wire are the entire point.
        /// </summary>
        public const string MessageName = "WatchPartyKeepAlive";

        private readonly object _syncRoot = new object();
        private readonly HashSet<string> _reachable =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly Func<string> _messageIdFactory;

        public ControlChannelKeepAlive(Func<string> messageIdFactory = null)
        {
            _messageIdFactory = messageIdFactory
                ?? (() => Guid.NewGuid().ToString("N"));
        }

        /// <summary>
        /// Writes one keepalive frame per session that still has a live WebSocket and
        /// reports which sessions changed reachability, so the log records the moment
        /// a control channel is lost rather than repeating itself every tick.
        /// </summary>
        public async Task<ControlChannelKeepAliveReport> SendAsync(
            IReadOnlyCollection<SessionInfo> sessions,
            CancellationToken cancellationToken)
        {
            if (sessions == null)
            {
                throw new ArgumentNullException(nameof(sessions));
            }

            var delivered = new List<string>();
            var unreachable = new List<string>();

            foreach (var session in sessions)
            {
                if (session == null || string.IsNullOrEmpty(session.Id))
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (await TrySendAsync(session, cancellationToken).ConfigureAwait(false))
                {
                    delivered.Add(session.Id);
                }
                else
                {
                    unreachable.Add(session.Id);
                }
            }

            return BuildReport(sessions, delivered, unreachable);
        }

        private async Task<bool> TrySendAsync(
            SessionInfo session,
            CancellationToken cancellationToken)
        {
            var controllers = OfficialIosWebSocketTransport
                .SelectActiveWebSocketControllers(session.SessionControllers);

            var delivered = false;
            foreach (var controller in controllers)
            {
                try
                {
                    await controller
                        .SendMessage(
                            MessageName.AsMemory(),
                            _messageIdFactory(),
                            string.Empty,
                            cancellationToken)
                        .ConfigureAwait(false);
                    delivered = true;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // A socket that fails the write is precisely a socket worth
                    // reporting as unreachable; the failure itself is the signal.
                }
            }

            return delivered;
        }

        /// <summary>
        /// Turns this tick's outcome into transitions and forgets sessions that are
        /// no longer in a party, so the tracked set cannot outgrow the live one.
        /// </summary>
        private ControlChannelKeepAliveReport BuildReport(
            IReadOnlyCollection<SessionInfo> sessions,
            List<string> delivered,
            List<string> unreachable)
        {
            lock (_syncRoot)
            {
                var lost = unreachable.Where(_reachable.Contains).ToList();
                var restored = delivered.Where(id => !_reachable.Contains(id)).ToList();

                _reachable.ExceptWith(unreachable);
                _reachable.UnionWith(delivered);
                _reachable.IntersectWith(sessions
                    .Where(session => session != null && !string.IsNullOrEmpty(session.Id))
                    .Select(session => session.Id));

                return new ControlChannelKeepAliveReport(delivered, lost, restored);
            }
        }

        public void Clear()
        {
            lock (_syncRoot)
            {
                _reachable.Clear();
            }
        }
    }

    public sealed class ControlChannelKeepAliveReport
    {
        public ControlChannelKeepAliveReport(
            IReadOnlyList<string> delivered,
            IReadOnlyList<string> lost,
            IReadOnlyList<string> restored)
        {
            Delivered = delivered;
            Lost = lost;
            Restored = restored;
        }

        /// <summary>Sessions this tick wrote a frame to.</summary>
        public IReadOnlyList<string> Delivered { get; }

        /// <summary>Sessions whose control channel went away since the last tick.</summary>
        public IReadOnlyList<string> Lost { get; }

        /// <summary>Sessions whose control channel came back since the last tick.</summary>
        public IReadOnlyList<string> Restored { get; }
    }
}
