using System;
using System.Collections.Generic;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Remembers which master session has successfully used the explicit Web seek
    /// endpoint. A browser tab that predates the dashboard patch still needs the
    /// legacy progress-jump fallback, but a patched tab must not let stale heartbeats
    /// move the authoritative clock after an explicit seek has been observed.
    /// </summary>
    public sealed class MasterSeekSourceTracker
    {
        private readonly object _syncRoot = new object();
        private readonly Dictionary<string, string> _explicitSessions =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public void MarkExplicit(string partyId, string sessionId)
        {
            if (string.IsNullOrEmpty(partyId) || string.IsNullOrEmpty(sessionId))
            {
                return;
            }

            lock (_syncRoot)
            {
                _explicitSessions[partyId] = sessionId;
            }
        }

        public bool HasExplicitForSession(string partyId, string sessionId)
        {
            if (string.IsNullOrEmpty(partyId) || string.IsNullOrEmpty(sessionId))
            {
                return false;
            }

            lock (_syncRoot)
            {
                return _explicitSessions.TryGetValue(partyId, out var explicitSessionId)
                    && string.Equals(explicitSessionId, sessionId, StringComparison.Ordinal);
            }
        }

        public void ClearParty(string partyId)
        {
            if (string.IsNullOrEmpty(partyId))
            {
                return;
            }

            lock (_syncRoot)
            {
                _explicitSessions.Remove(partyId);
            }
        }
    }
}
