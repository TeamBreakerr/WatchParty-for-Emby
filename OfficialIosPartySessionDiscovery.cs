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
        public static IReadOnlyList<SessionInfo> Select(
            IEnumerable<SessionInfo> sessions,
            string masterSessionId,
            Func<SessionInfo, bool> canJoin)
        {
            if (canJoin == null)
            {
                throw new ArgumentNullException(nameof(canJoin));
            }

            return (sessions ?? Array.Empty<SessionInfo>())
                .Where(session => session != null
                    && !string.IsNullOrWhiteSpace(session.Id)
                    && !string.IsNullOrWhiteSpace(session.UserId)
                    && string.Equals(
                        session.Client,
                        "Emby for iOS",
                        StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(
                        session.Id,
                        masterSessionId,
                        StringComparison.Ordinal)
                    && canJoin(session))
                .GroupBy(session => session.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
        }
    }
}
