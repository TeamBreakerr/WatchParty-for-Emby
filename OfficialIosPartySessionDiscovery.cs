using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Session;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Selects online official-iOS sessions for an explicit room PlayNow command.
    /// Session identity, rather than user identity, defines the playback role.
    /// </summary>
    public static class OfficialIosPartySessionDiscovery
    {
        public static IReadOnlyList<SessionInfo> Discover(IEnumerable<SessionInfo> sessions)
        {
            return (sessions ?? Array.Empty<SessionInfo>())
                .Where(session => session != null
                    && !string.IsNullOrWhiteSpace(session.Id)
                    && !string.IsNullOrWhiteSpace(session.UserId)
                    && OfficialIosWebSocketTransport.IsOfficialIosClient(
                        session.Client))
                .GroupBy(session => session.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
        }

        public static IReadOnlyList<SessionInfo> Select(
            IEnumerable<SessionInfo> sessions,
            string masterSessionId,
            Func<SessionInfo, bool> canJoin)
        {
            if (canJoin == null)
            {
                throw new ArgumentNullException(nameof(canJoin));
            }

            return Discover(sessions)
                .Where(session => !string.Equals(
                        session.Id,
                        masterSessionId,
                        StringComparison.Ordinal)
                    && canJoin(session))
                .ToList();
        }

        public static IReadOnlyList<SessionInfo> SelectRequested(
            IEnumerable<SessionInfo> sessions,
            IEnumerable<string> requestedSessionIds,
            Func<SessionInfo, bool> canReceiveLaunchCommand)
        {
            if (canReceiveLaunchCommand == null)
            {
                throw new ArgumentNullException(nameof(canReceiveLaunchCommand));
            }

            var requested = new HashSet<string>(
                (requestedSessionIds ?? Array.Empty<string>())
                    .Where(sessionId => !string.IsNullOrWhiteSpace(sessionId)),
                StringComparer.Ordinal);
            if (requested.Count == 0)
            {
                return Array.Empty<SessionInfo>();
            }

            return Discover(sessions)
                .Where(session => requested.Contains(session.Id)
                    && canReceiveLaunchCommand(session))
                .ToList();
        }
    }
}
