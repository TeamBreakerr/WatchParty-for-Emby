using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Session;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Discovers currently online Emby sessions for explicit PlayNow commands.
    /// Session identity, rather than user or client name, defines the target.
    /// </summary>
    public static class PartySessionDiscovery
    {
        public static IReadOnlyList<SessionInfo> Discover(
            IEnumerable<SessionInfo> sessions,
            DateTime nowUtc)
        {
            return (sessions ?? Array.Empty<SessionInfo>())
                .Where(session => PartySessionLivenessPolicy.IsOnline(session, nowUtc))
                .GroupBy(session => session.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
        }

        public static IReadOnlyList<SessionInfo> SelectRequested(
            IEnumerable<SessionInfo> sessions,
            DateTime nowUtc,
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

            return Discover(sessions, nowUtc)
                .Where(session => requested.Contains(session.Id)
                    && canReceiveLaunchCommand(session))
                .ToList();
        }
    }
}
