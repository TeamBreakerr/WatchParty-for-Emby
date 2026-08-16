using System;
using System.Collections.Generic;
using System.Linq;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Tracks watch-party playback sessions keyed by Emby SessionId instead of UserId.
    /// The same user can have several active sessions (official iOS app, VidHub, Conflux,
    /// web player, ...) without one session overwriting or evicting another.
    /// </summary>
    public sealed class PartySessionRegistry
    {
        private readonly object _syncRoot = new object();
        private readonly Dictionary<string, Dictionary<string, PartyParticipant>> _sessionsByParty =
            new Dictionary<string, Dictionary<string, PartyParticipant>>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _masterSessions =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public PartyParticipant AddOrUpdate(string partyId, string sessionId, PartyParticipant participant)
        {
            if (string.IsNullOrEmpty(partyId) || string.IsNullOrEmpty(sessionId) || participant == null)
            {
                throw new ArgumentException("partyId, sessionId and participant are required");
            }

            lock (_syncRoot)
            {
                if (!_sessionsByParty.TryGetValue(partyId, out var sessions))
                {
                    sessions = new Dictionary<string, PartyParticipant>(StringComparer.Ordinal);
                    _sessionsByParty[partyId] = sessions;
                }

                participant.SessionId = sessionId;
                sessions[sessionId] = participant;
                return participant;
            }
        }

        public bool TryGetSession(string partyId, string sessionId, out PartyParticipant participant)
        {
            lock (_syncRoot)
            {
                participant = null;
                return !string.IsNullOrEmpty(partyId)
                    && !string.IsNullOrEmpty(sessionId)
                    && _sessionsByParty.TryGetValue(partyId, out var sessions)
                    && sessions.TryGetValue(sessionId, out participant);
            }
        }

        public bool TryGetLatestSessionForUser(string partyId, string userId, out PartyParticipant participant)
        {
            lock (_syncRoot)
            {
                participant = null;
                if (string.IsNullOrEmpty(partyId)
                    || string.IsNullOrEmpty(userId)
                    || !_sessionsByParty.TryGetValue(partyId, out var sessions))
                {
                    return false;
                }

                PartyParticipant best = null;
                foreach (var candidate in sessions.Values)
                {
                    if (!string.Equals(candidate.UserId, userId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (best == null || candidate.LastActivityAt > best.LastActivityAt)
                    {
                        best = candidate;
                    }
                }

                participant = best;
                return best != null;
            }
        }

        public IReadOnlyList<PartyParticipant> GetSessions(string partyId)
        {
            lock (_syncRoot)
            {
                return !string.IsNullOrEmpty(partyId)
                    && _sessionsByParty.TryGetValue(partyId, out var sessions)
                        ? sessions.Values.ToList()
                        : Array.Empty<PartyParticipant>();
            }
        }

        public IReadOnlyList<string> GetSessionIdsForUser(string partyId, string userId)
        {
            lock (_syncRoot)
            {
                if (string.IsNullOrEmpty(partyId)
                    || string.IsNullOrEmpty(userId)
                    || !_sessionsByParty.TryGetValue(partyId, out var sessions))
                {
                    return Array.Empty<string>();
                }

                return sessions.Values
                    .Where(p => string.Equals(p.UserId, userId, StringComparison.Ordinal))
                    .Select(p => p.SessionId)
                    .Where(sessionId => !string.IsNullOrEmpty(sessionId))
                    .ToList();
            }
        }

        public bool HasUser(string partyId, string userId)
        {
            lock (_syncRoot)
            {
                if (string.IsNullOrEmpty(partyId)
                    || string.IsNullOrEmpty(userId)
                    || !_sessionsByParty.TryGetValue(partyId, out var sessions))
                {
                    return false;
                }

                return sessions.Values.Any(p => string.Equals(p.UserId, userId, StringComparison.Ordinal));
            }
        }

        public int SessionCount(string partyId)
        {
            lock (_syncRoot)
            {
                return !string.IsNullOrEmpty(partyId)
                    && _sessionsByParty.TryGetValue(partyId, out var sessions)
                        ? sessions.Count
                        : 0;
            }
        }

        public int DistinctUserCount(string partyId)
        {
            lock (_syncRoot)
            {
                if (string.IsNullOrEmpty(partyId)
                    || !_sessionsByParty.TryGetValue(partyId, out var sessions))
                {
                    return 0;
                }

                var users = new HashSet<string>(StringComparer.Ordinal);
                foreach (var participant in sessions.Values)
                {
                    if (!string.IsNullOrEmpty(participant.UserId))
                    {
                        users.Add(participant.UserId);
                    }
                }

                return users.Count;
            }
        }

        /// <summary>
        /// Removes exactly one session. Never removes other sessions of the same user.
        /// Returns whether the removed session was the registered master session.
        /// </summary>
        public bool TryRemoveSession(
            string partyId,
            string sessionId,
            out PartyParticipant removed,
            out bool wasMaster)
        {
            lock (_syncRoot)
            {
                removed = null;
                wasMaster = false;
                if (string.IsNullOrEmpty(partyId)
                    || string.IsNullOrEmpty(sessionId)
                    || !_sessionsByParty.TryGetValue(partyId, out var sessions)
                    || !sessions.TryGetValue(sessionId, out removed))
                {
                    return false;
                }

                sessions.Remove(sessionId);
                if (sessions.Count == 0)
                {
                    _sessionsByParty.Remove(partyId);
                }

                if (_masterSessions.TryGetValue(partyId, out var masterSessionId)
                    && string.Equals(masterSessionId, sessionId, StringComparison.Ordinal))
                {
                    _masterSessions.Remove(partyId);
                    wasMaster = true;
                }

                return true;
            }
        }

        public bool SetMasterSession(string partyId, string sessionId)
        {
            lock (_syncRoot)
            {
                if (string.IsNullOrEmpty(partyId)
                    || string.IsNullOrEmpty(sessionId)
                    || !_sessionsByParty.TryGetValue(partyId, out var sessions)
                    || !sessions.ContainsKey(sessionId))
                {
                    return false;
                }

                _masterSessions[partyId] = sessionId;
                return true;
            }
        }

        /// <summary>
        /// Registers a master session only when the party does not already have one.
        /// A second session from the same master user never overwrites the active master.
        /// </summary>
        public bool SetMasterSessionIfAbsent(string partyId, string sessionId)
        {
            lock (_syncRoot)
            {
                if (string.IsNullOrEmpty(partyId)
                    || string.IsNullOrEmpty(sessionId)
                    || !_sessionsByParty.TryGetValue(partyId, out var sessions)
                    || !sessions.ContainsKey(sessionId))
                {
                    return false;
                }

                if (_masterSessions.TryGetValue(partyId, out var currentMaster)
                    && !string.IsNullOrEmpty(currentMaster))
                {
                    return false;
                }

                _masterSessions[partyId] = sessionId;
                return true;
            }
        }

        public string GetMasterSession(string partyId)
        {
            lock (_syncRoot)
            {
                return !string.IsNullOrEmpty(partyId)
                    && _masterSessions.TryGetValue(partyId, out var sessionId)
                        ? sessionId
                        : null;
            }
        }

        public bool IsMasterSession(string partyId, string sessionId)
        {
            var masterSessionId = GetMasterSession(partyId);
            return !string.IsNullOrEmpty(masterSessionId)
                && !string.IsNullOrEmpty(sessionId)
                && string.Equals(masterSessionId, sessionId, StringComparison.Ordinal);
        }

        /// <summary>
        /// Promotes the most recently active remaining session of a user to master.
        /// Used when the old master session stops but the same user is still watching.
        /// </summary>
        public bool PromoteLatestSessionForUser(
            string partyId,
            string userId,
            out string promotedSessionId)
        {
            lock (_syncRoot)
            {
                promotedSessionId = null;
                if (string.IsNullOrEmpty(partyId)
                    || string.IsNullOrEmpty(userId)
                    || !_sessionsByParty.TryGetValue(partyId, out var sessions))
                {
                    return false;
                }

                PartyParticipant best = null;
                foreach (var candidate in sessions.Values)
                {
                    if (!string.Equals(candidate.UserId, userId, StringComparison.Ordinal)
                        || string.IsNullOrEmpty(candidate.SessionId))
                    {
                        continue;
                    }

                    if (best == null || candidate.LastActivityAt > best.LastActivityAt)
                    {
                        best = candidate;
                    }
                }

                if (best == null)
                {
                    return false;
                }

                _masterSessions[partyId] = best.SessionId;
                promotedSessionId = best.SessionId;
                return true;
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
                _sessionsByParty.Remove(partyId);
                _masterSessions.Remove(partyId);
            }
        }
    }
}
