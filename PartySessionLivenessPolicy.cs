using System;
using System.Linq;
using MediaBrowser.Controller.Session;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Converts Emby's retained SessionInfo objects into a user-visible online
    /// state. SessionInfo can outlive a client process, so object presence alone
    /// must never be treated as online.
    /// </summary>
    public static class PartySessionLivenessPolicy
    {
        public static readonly TimeSpan DefaultOnlineWindow =
            TimeSpan.FromMinutes(3);
        public static readonly TimeSpan DefaultParticipantReportWindow =
            TimeSpan.FromSeconds(30);

        public static bool IsWebClient(string client)
        {
            return client?.IndexOf("Web", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsOnline(
            SessionInfo session,
            DateTime nowUtc,
            TimeSpan? onlineWindow = null)
        {
            if (!HasSessionIdentity(session))
            {
                return false;
            }

            // The control WebSocket is a capability, not the only presence signal.
            // Emby keeps SessionInfo and its HTTP playback activity alive while an
            // iOS client briefly rebuilds its socket (for example after returning
            // from background). Keep that session visible during the normal activity
            // window, but let launch/command eligibility separately require the live
            // WebSocket. A force-quit session has no new activity and expires here.
            var isIos = OfficialIosWebSocketTransport.IsOfficialIosClient(session.Client);
            if (isIos)
            {
                return OfficialIosWebSocketTransport.HasActiveWebSocketController(
                        session.SessionControllers)
                    || HasRecentActivity(session, nowUtc, onlineWindow);
            }

            // An active controller is stronger than LastActivityDate. In particular,
            // an idle Web tab can keep its control channel open for much longer than
            // the fallback window and must remain available for one-click launch.
            if (HasActiveController(session))
            {
                return true;
            }

            // Once Emby has supplied controller objects, their active flags are the
            // connection truth. A recently-active SessionInfo with only inactive
            // retained controllers represents a closed client, not an online one.
            if (HasController(session))
            {
                return false;
            }

            // Some non-iOS clients do not expose a persistent controller. Keep a
            // bounded activity fallback for them, while retained sessions eventually
            // expire instead of remaining online forever.
            return HasRecentActivity(session, nowUtc, onlineWindow);
        }

        public static bool HasRecentActivity(
            SessionInfo session,
            DateTime nowUtc,
            TimeSpan? onlineWindow = null)
        {
            if (!HasSessionIdentity(session))
            {
                return false;
            }

            var window = onlineWindow ?? DefaultOnlineWindow;
            if (window <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(onlineWindow));
            }

            var lastActivityUtc = session.LastActivityDate.UtcDateTime;
            if (lastActivityUtc == DateTime.MinValue)
            {
                // A newly-created session can reach the plugin before Emby writes
                // its first activity timestamp. An active session controller is a
                // safe, short-lived fallback for that initial handshake.
                return HasActiveController(session);
            }

            return nowUtc >= lastActivityUtc.AddMinutes(-1)
                && nowUtc - lastActivityUtc <= window;
        }

        /// <summary>
        /// A room participant can still be demonstrably online while its remote-control
        /// WebSocket is unavailable. PlaybackStart/Progress reaches the plugin through a
        /// separate HTTP path and updates the participant registry only after playback-
        /// generation validation. Keep that trusted reporter online for one short report
        /// window without pretending that it can receive commands.
        /// </summary>
        public static bool IsParticipantOnline(
            SessionInfo session,
            DateTime lastAcceptedPlaybackActivityUtc,
            DateTime nowUtc,
            TimeSpan? reportWindow = null)
        {
            if (!HasSessionIdentity(session))
            {
                return false;
            }

            if (IsOnline(session, nowUtc))
            {
                return true;
            }

            var window = reportWindow ?? DefaultParticipantReportWindow;
            if (window <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(reportWindow));
            }
            if (lastAcceptedPlaybackActivityUtc == DateTime.MinValue)
            {
                return false;
            }

            return nowUtc >= lastAcceptedPlaybackActivityUtc.AddMinutes(-1)
                && nowUtc - lastAcceptedPlaybackActivityUtc <= window;
        }

        private static bool HasActiveController(SessionInfo session)
        {
            return session?.SessionControllers?.Any(controller =>
                controller != null && controller.IsSessionActive) == true;
        }

        private static bool HasController(SessionInfo session)
        {
            return session?.SessionControllers?.Any(controller =>
                controller != null) == true;
        }

        private static bool HasSessionIdentity(SessionInfo session)
        {
            return session != null
                && !string.IsNullOrWhiteSpace(session.Id)
                && !string.IsNullOrWhiteSpace(session.UserId);
        }
    }
}
