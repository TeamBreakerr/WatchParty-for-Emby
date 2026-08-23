using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Session;

namespace WatchPartyForEmby
{
    public static class SessionInfoIndex
    {
        public static Dictionary<string, SessionInfo> Build(IEnumerable<SessionInfo> sessions)
        {
            return (sessions ?? Array.Empty<SessionInfo>())
                .Where(session => session != null && !string.IsNullOrEmpty(session.Id))
                .GroupBy(session => session.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        }
    }
}
