using System;
using System.Collections.Generic;
using System.Linq;

namespace WatchPartyForEmby
{
    public sealed class SeriesPlaybackHandoffSession
    {
        public SeriesPlaybackHandoffSession(
            string sessionId,
            string playSessionId,
            DateTime? authorizationExpiresAtUtc = null)
        {
            if (string.IsNullOrWhiteSpace(sessionId)
                || string.IsNullOrWhiteSpace(playSessionId))
            {
                throw new ArgumentException("sessionId and playSessionId are required");
            }

            SessionId = sessionId;
            PlaySessionId = playSessionId;
            AuthorizationExpiresAtUtc = authorizationExpiresAtUtc;
        }

        public string SessionId { get; }
        public string PlaySessionId { get; }
        public DateTime? AuthorizationExpiresAtUtc { get; }

        public bool IsValidAt(DateTime observedAtUtc)
        {
            return !AuthorizationExpiresAtUtc.HasValue
                || observedAtUtc <= AuthorizationExpiresAtUtc.Value;
        }
    }

    /// <summary>
    /// Retains the exact follower sessions that were actively playing when a series
    /// master stopped. Some Emby clients report the old episode's Stop several seconds
    /// before the next episode's Start; this bounded handoff lets that next Start revive
    /// only those followers, without pulling older dormant room registrations back in.
    /// </summary>
    public sealed class SeriesPlaybackHandoffTracker
    {
        private readonly object _syncRoot = new object();
        private readonly TimeSpan _handoffWindow;
        private readonly Dictionary<string, Handoff> _handoffs =
            new Dictionary<string, Handoff>(StringComparer.Ordinal);

        public SeriesPlaybackHandoffTracker(TimeSpan handoffWindow)
        {
            if (handoffWindow <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(handoffWindow));
            }

            _handoffWindow = handoffWindow;
        }

        public void Capture(
            string partyId,
            string masterUserId,
            IEnumerable<SeriesPlaybackHandoffSession> activeFollowerSessions,
            DateTime stoppedAtUtc)
        {
            if (string.IsNullOrEmpty(partyId) || string.IsNullOrEmpty(masterUserId))
            {
                throw new ArgumentException("partyId and masterUserId are required");
            }

            var sessionsById = (activeFollowerSessions
                    ?? Array.Empty<SeriesPlaybackHandoffSession>())
                .Where(session => session != null && session.IsValidAt(stoppedAtUtc))
                .GroupBy(session => session.SessionId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Last(),
                    StringComparer.Ordinal);

            lock (_syncRoot)
            {
                if (sessionsById.Count == 0)
                {
                    _handoffs.Remove(partyId);
                    return;
                }

                _handoffs[partyId] = new Handoff(
                    masterUserId,
                    sessionsById.Values.ToArray(),
                    stoppedAtUtc.Add(_handoffWindow));
            }
        }

        public IReadOnlyCollection<SeriesPlaybackHandoffSession> Consume(
            string partyId,
            string masterUserId,
            DateTime startedAtUtc,
            Func<SeriesPlaybackHandoffSession, bool> isStillEligible = null)
        {
            if (string.IsNullOrEmpty(partyId) || string.IsNullOrEmpty(masterUserId))
            {
                return Array.Empty<SeriesPlaybackHandoffSession>();
            }

            Handoff handoff;
            lock (_syncRoot)
            {
                if (!_handoffs.TryGetValue(partyId, out handoff))
                {
                    return Array.Empty<SeriesPlaybackHandoffSession>();
                }

                if (handoff.ExpiresAtUtc < startedAtUtc)
                {
                    _handoffs.Remove(partyId);
                    return Array.Empty<SeriesPlaybackHandoffSession>();
                }

                if (!string.Equals(
                        handoff.MasterUserId,
                        masterUserId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Array.Empty<SeriesPlaybackHandoffSession>();
                }

                _handoffs.Remove(partyId);
            }

            // Eligibility can inspect the participant registry, live sessions, and
            // plugin configuration. Run it after releasing the tracker lock so room
            // cleanup can never acquire those locks in the opposite order.
            return handoff.Sessions
                .Where(session => session.IsValidAt(startedAtUtc))
                .Where(session => isStillEligible == null || isStillEligible(session))
                .ToArray();
        }

        public void ClearParty(string partyId)
        {
            if (string.IsNullOrEmpty(partyId))
            {
                return;
            }

            lock (_syncRoot)
            {
                _handoffs.Remove(partyId);
            }
        }

        private sealed class Handoff
        {
            public Handoff(
                string masterUserId,
                IReadOnlyCollection<SeriesPlaybackHandoffSession> sessions,
                DateTime expiresAtUtc)
            {
                MasterUserId = masterUserId;
                Sessions = sessions;
                ExpiresAtUtc = expiresAtUtc;
            }

            public string MasterUserId { get; }
            public IReadOnlyCollection<SeriesPlaybackHandoffSession> Sessions { get; }
            public DateTime ExpiresAtUtc { get; }
        }
    }
}
