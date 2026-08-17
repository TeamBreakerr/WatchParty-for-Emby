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
        private const int DefaultMaxRetiredPlaybackIdsPerParty = 256;
        private static readonly TimeSpan ReplacementPlaybackSilence = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan ReplacementCandidateObservation = TimeSpan.FromSeconds(2);
        private static readonly long ReplacementBackwardToleranceTicks = TimeSpan.TicksPerSecond * 2;
        private readonly object _syncRoot = new object();
        private readonly int _maxRetiredPlaybackIdsPerParty;
        private readonly Dictionary<string, Dictionary<string, PartyParticipant>> _sessionsByParty =
            new Dictionary<string, Dictionary<string, PartyParticipant>>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _masterSessions =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, HashSet<string>>> _retiredPlaybackIds =
            new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Queue<KeyValuePair<string, string>>> _retiredPlaybackOrder =
            new Dictionary<string, Queue<KeyValuePair<string, string>>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, ProgressAdoptionCandidate>> _progressAdoptions =
            new Dictionary<string, Dictionary<string, ProgressAdoptionCandidate>>(StringComparer.Ordinal);

        public PartySessionRegistry(
            int maxRetiredPlaybackIdsPerParty = DefaultMaxRetiredPlaybackIdsPerParty)
        {
            if (maxRetiredPlaybackIdsPerParty <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxRetiredPlaybackIdsPerParty),
                    "Retired playback history capacity must be positive.");
            }

            _maxRetiredPlaybackIdsPerParty = maxRetiredPlaybackIdsPerParty;
        }

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

                var stored = Clone(participant);
                stored.SessionId = sessionId;
                if (sessions.TryGetValue(sessionId, out var previous))
                {
                    RetirePreviousPlayback(partyId, sessionId, previous.PlaySessionId, stored.PlaySessionId);
                }
                sessions[sessionId] = stored;
                ClearProgressAdoption(partyId, sessionId);
                return Clone(stored);
            }
        }

        /// <summary>
        /// Atomically creates a session or refreshes its playback identity while
        /// preserving its latest position and pause state.
        /// </summary>
        public PartyParticipant UpsertSession(
            string partyId,
            string sessionId,
            string userId,
            string userName,
            string playSessionId,
            DateTime nowUtc,
            out string previousPlaySessionId,
            out bool created)
        {
            if (string.IsNullOrEmpty(partyId) || string.IsNullOrEmpty(sessionId))
            {
                throw new ArgumentException("partyId and sessionId are required");
            }

            lock (_syncRoot)
            {
                if (!_sessionsByParty.TryGetValue(partyId, out var sessions))
                {
                    sessions = new Dictionary<string, PartyParticipant>(StringComparer.Ordinal);
                    _sessionsByParty[partyId] = sessions;
                }

                created = !sessions.TryGetValue(sessionId, out var participant);
                previousPlaySessionId = created ? null : participant.PlaySessionId;
                if (created)
                {
                    participant = new PartyParticipant
                    {
                        JoinedAt = nowUtc
                    };
                    sessions[sessionId] = participant;
                }

                participant.UserId = userId;
                participant.UserName = userName;
                participant.SessionId = sessionId;
                participant.LastActivityAt = nowUtc;
                if (!string.IsNullOrEmpty(playSessionId))
                {
                    RetirePreviousPlayback(
                        partyId,
                        sessionId,
                        participant.PlaySessionId,
                        playSessionId);
                    participant.PlaySessionId = playSessionId;
                    ClearProgressAdoption(partyId, sessionId);
                }

                return Clone(participant);
            }
        }

        /// <summary>
        /// Atomically enforces the party's distinct-user capacity and upserts a
        /// playback session. A non-positive capacity means unlimited participants.
        /// </summary>
        public bool TryUpsertSession(
            string partyId,
            string sessionId,
            string userId,
            string userName,
            string playSessionId,
            DateTime nowUtc,
            int maxParticipants,
            out PartyParticipant participant,
            out string previousPlaySessionId,
            out bool created)
        {
            if (string.IsNullOrEmpty(partyId) || string.IsNullOrEmpty(sessionId))
            {
                throw new ArgumentException("partyId and sessionId are required");
            }

            lock (_syncRoot)
            {
                participant = null;
                previousPlaySessionId = null;
                created = false;
                if (maxParticipants > 0
                    && !HasUser(partyId, userId)
                    && DistinctUserCount(partyId) >= maxParticipants)
                {
                    return false;
                }

                participant = UpsertSession(
                    partyId,
                    sessionId,
                    userId,
                    userName,
                    playSessionId,
                    nowUtc,
                    out previousPlaySessionId,
                    out created);
                return true;
            }
        }

        public bool UpdateActivity(
            string partyId,
            string sessionId,
            long positionTicks,
            bool isPaused,
            DateTime nowUtc)
        {
            lock (_syncRoot)
            {
                if (string.IsNullOrEmpty(partyId)
                    || string.IsNullOrEmpty(sessionId)
                    || !_sessionsByParty.TryGetValue(partyId, out var sessions)
                    || !sessions.TryGetValue(sessionId, out var participant))
                {
                    return false;
                }

                participant.LastActivityAt = nowUtc;
                participant.CurrentPositionTicks = Math.Max(0, positionTicks);
                participant.IsPaused = isPaused;
                return true;
            }
        }

        public bool ResetEpisodeState(string partyId)
        {
            lock (_syncRoot)
            {
                if (string.IsNullOrEmpty(partyId)
                    || !_sessionsByParty.TryGetValue(partyId, out var sessions))
                {
                    return false;
                }

                foreach (var participant in sessions.Values)
                {
                    participant.CurrentPositionTicks = 0;
                    participant.IsPaused = false;
                    participant.IsBuffering = false;
                }

                return true;
            }
        }

        public bool SetBuffering(string partyId, string sessionId, bool isBuffering)
        {
            lock (_syncRoot)
            {
                if (string.IsNullOrEmpty(partyId)
                    || string.IsNullOrEmpty(sessionId)
                    || !_sessionsByParty.TryGetValue(partyId, out var sessions)
                    || !sessions.TryGetValue(sessionId, out var participant))
                {
                    return false;
                }

                participant.IsBuffering = isBuffering;
                return true;
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
                    && sessions.TryGetValue(sessionId, out var stored)
                    && (participant = Clone(stored)) != null;
            }
        }

        /// <summary>
        /// Returns false only when both the registered participant and the incoming
        /// progress report identify different playback instances. Emby can reuse one
        /// SessionId while an old PlaySessionId continues reporting progress; accepting
        /// that stale report would replace the current playback and move the party clock.
        /// Position-less StateChange reports may omit PlaySessionId, so an empty value is
        /// not sufficient evidence that the event is stale.
        /// </summary>
        public bool IsCurrentPlaybackSession(
            string partyId,
            string sessionId,
            string playSessionId)
        {
            lock (_syncRoot)
            {
                if (string.IsNullOrEmpty(partyId)
                    || string.IsNullOrEmpty(sessionId)
                    || !_sessionsByParty.TryGetValue(partyId, out var sessions)
                    || !sessions.TryGetValue(sessionId, out var participant)
                    || string.IsNullOrEmpty(participant.PlaySessionId)
                    || string.IsNullOrEmpty(playSessionId))
                {
                    return true;
                }

                return string.Equals(
                    participant.PlaySessionId,
                    playSessionId,
                    StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// Accepts progress for the current playback, or establishes the playback id when
        /// the participant has no identity because PlaybackStart was omitted. Once a
        /// playback id is known, an unseen id must provide multiple position-bearing
        /// reports over time while the current identity remains silent. This lets a client
        /// recover when PlaybackStart and Stop were both lost without allowing a burst of
        /// delayed callbacks from an old Emby playback to reclaim the SessionId.
        /// </summary>
        public bool TryAcceptPlaybackProgress(
            string partyId,
            string sessionId,
            string playSessionId,
            out bool adopted,
            out string previousPlaySessionId)
        {
            return TryAcceptPlaybackProgress(
                partyId,
                sessionId,
                playSessionId,
                reportedPositionTicks: null,
                DateTime.UtcNow,
                out adopted,
                out previousPlaySessionId);
        }

        public bool TryAcceptPlaybackProgress(
            string partyId,
            string sessionId,
            string playSessionId,
            long? reportedPositionTicks,
            DateTime nowUtc,
            out bool adopted,
            out string previousPlaySessionId)
        {
            lock (_syncRoot)
            {
                adopted = false;
                previousPlaySessionId = null;
                if (string.IsNullOrEmpty(partyId)
                    || string.IsNullOrEmpty(sessionId)
                    || string.IsNullOrEmpty(playSessionId)
                    || !_sessionsByParty.TryGetValue(partyId, out var sessions)
                    || !sessions.TryGetValue(sessionId, out var participant))
                {
                    return true;
                }

                previousPlaySessionId = participant.PlaySessionId;
                if (IsRetiredPlayback(partyId, sessionId, playSessionId))
                {
                    ClearProgressAdoption(partyId, sessionId);
                    return false;
                }

                if (string.IsNullOrEmpty(previousPlaySessionId))
                {
                    participant.PlaySessionId = playSessionId;
                    ClearProgressAdoption(partyId, sessionId);
                    adopted = true;
                    return true;
                }

                if (string.Equals(
                        previousPlaySessionId,
                        playSessionId,
                        StringComparison.Ordinal))
                {
                    ClearProgressAdoption(partyId, sessionId);
                    return true;
                }

                if (!reportedPositionTicks.HasValue
                    || !ObserveProgressAdoptionCandidate(
                        partyId,
                        sessionId,
                        playSessionId,
                        reportedPositionTicks.Value,
                        nowUtc,
                        out var candidate)
                    || nowUtc - participant.LastActivityAt < ReplacementPlaybackSilence
                    || nowUtc - candidate.FirstObservedAt < ReplacementCandidateObservation)
                {
                    return false;
                }

                RetirePreviousPlayback(
                    partyId,
                    sessionId,
                    previousPlaySessionId,
                    playSessionId);
                participant.PlaySessionId = playSessionId;
                ClearProgressAdoption(partyId, sessionId);
                adopted = true;
                return true;
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

                participant = Clone(best);
                return best != null;
            }
        }

        public IReadOnlyList<PartyParticipant> GetSessions(string partyId)
        {
            lock (_syncRoot)
            {
                return !string.IsNullOrEmpty(partyId)
                    && _sessionsByParty.TryGetValue(partyId, out var sessions)
                        ? sessions.Values.Select(Clone).ToList()
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
            return TryRemoveSessionInternal(
                partyId,
                sessionId,
                expectedPlaySessionId: null,
                requirePlaySessionMatch: false,
                out removed,
                out wasMaster);
        }

        /// <summary>
        /// Removes a session only when it still represents the expected playback
        /// instance. Emby clients reuse SessionId across plays, so a delayed Stop for an
        /// old PlaySessionId must not evict the replacement playback from the party.
        /// </summary>
        public bool TryRemoveSession(
            string partyId,
            string sessionId,
            string expectedPlaySessionId,
            out PartyParticipant removed,
            out bool wasMaster)
        {
            return TryRemoveSessionInternal(
                partyId,
                sessionId,
                expectedPlaySessionId,
                requirePlaySessionMatch: true,
                out removed,
                out wasMaster);
        }

        public bool TryRemoveSessionIfInactive(
            string partyId,
            string sessionId,
            DateTime inactiveBeforeUtc,
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
                    || !sessions.TryGetValue(sessionId, out var participant)
                    || participant.LastActivityAt >= inactiveBeforeUtc)
                {
                    return false;
                }

                return TryRemoveSessionInternal(
                    partyId,
                    sessionId,
                    expectedPlaySessionId: null,
                    requirePlaySessionMatch: false,
                    out removed,
                    out wasMaster);
            }
        }

        private bool TryRemoveSessionInternal(
            string partyId,
            string sessionId,
            string expectedPlaySessionId,
            bool requirePlaySessionMatch,
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
                    || !sessions.TryGetValue(sessionId, out var stored))
                {
                    return false;
                }

                if (requirePlaySessionMatch
                    && !string.IsNullOrEmpty(stored.PlaySessionId)
                    && !string.Equals(
                        stored.PlaySessionId,
                        expectedPlaySessionId,
                        StringComparison.Ordinal))
                {
                    return false;
                }

                RetirePlayback(partyId, sessionId, stored.PlaySessionId);
                ClearProgressAdoption(partyId, sessionId);
                sessions.Remove(sessionId);
                removed = Clone(stored);
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
                _retiredPlaybackIds.Remove(partyId);
                _retiredPlaybackOrder.Remove(partyId);
                _progressAdoptions.Remove(partyId);
            }
        }

        private bool ObserveProgressAdoptionCandidate(
            string partyId,
            string sessionId,
            string playSessionId,
            long reportedPositionTicks,
            DateTime nowUtc,
            out ProgressAdoptionCandidate candidate)
        {
            if (!_progressAdoptions.TryGetValue(partyId, out var candidatesBySession))
            {
                candidatesBySession = new Dictionary<string, ProgressAdoptionCandidate>(StringComparer.Ordinal);
                _progressAdoptions[partyId] = candidatesBySession;
            }

            if (!candidatesBySession.TryGetValue(sessionId, out candidate)
                || !string.Equals(candidate.PlaySessionId, playSessionId, StringComparison.Ordinal)
                || nowUtc <= candidate.LastObservedAt
                || reportedPositionTicks + ReplacementBackwardToleranceTicks
                    < candidate.LastPositionTicks)
            {
                candidate = new ProgressAdoptionCandidate
                {
                    PlaySessionId = playSessionId,
                    FirstObservedAt = nowUtc,
                    LastObservedAt = nowUtc,
                    LastPositionTicks = Math.Max(0, reportedPositionTicks),
                    ObservationCount = 1
                };
                candidatesBySession[sessionId] = candidate;
                return false;
            }

            candidate.LastObservedAt = nowUtc;
            candidate.LastPositionTicks = Math.Max(0, reportedPositionTicks);
            candidate.ObservationCount++;
            return candidate.ObservationCount >= 2;
        }

        private void ClearProgressAdoption(string partyId, string sessionId)
        {
            if (!_progressAdoptions.TryGetValue(partyId, out var candidatesBySession))
            {
                return;
            }

            candidatesBySession.Remove(sessionId);
            if (candidatesBySession.Count == 0)
            {
                _progressAdoptions.Remove(partyId);
            }
        }

        private void RetirePreviousPlayback(
            string partyId,
            string sessionId,
            string previousPlaySessionId,
            string newPlaySessionId)
        {
            if (string.IsNullOrEmpty(previousPlaySessionId)
                || string.IsNullOrEmpty(newPlaySessionId)
                || string.Equals(
                    previousPlaySessionId,
                    newPlaySessionId,
                    StringComparison.Ordinal))
            {
                return;
            }

            RetirePlayback(partyId, sessionId, previousPlaySessionId);
        }

        private void RetirePlayback(
            string partyId,
            string sessionId,
            string playSessionId)
        {
            if (string.IsNullOrEmpty(playSessionId))
            {
                return;
            }

            if (!_retiredPlaybackIds.TryGetValue(partyId, out var retiredBySession))
            {
                retiredBySession = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                _retiredPlaybackIds[partyId] = retiredBySession;
            }

            if (!retiredBySession.TryGetValue(sessionId, out var retiredIds))
            {
                retiredIds = new HashSet<string>(StringComparer.Ordinal);
                retiredBySession[sessionId] = retiredIds;
            }

            if (!retiredIds.Add(playSessionId))
            {
                return;
            }

            if (!_retiredPlaybackOrder.TryGetValue(partyId, out var retiredOrder))
            {
                retiredOrder = new Queue<KeyValuePair<string, string>>();
                _retiredPlaybackOrder[partyId] = retiredOrder;
            }

            retiredOrder.Enqueue(
                new KeyValuePair<string, string>(sessionId, playSessionId));
            while (retiredOrder.Count > _maxRetiredPlaybackIdsPerParty)
            {
                var oldest = retiredOrder.Dequeue();
                if (retiredBySession.TryGetValue(oldest.Key, out var oldestSessionIds))
                {
                    oldestSessionIds.Remove(oldest.Value);
                    if (oldestSessionIds.Count == 0)
                    {
                        retiredBySession.Remove(oldest.Key);
                    }
                }
            }
        }

        private bool IsRetiredPlayback(string partyId, string sessionId, string playSessionId)
        {
            return _retiredPlaybackIds.TryGetValue(partyId, out var retiredBySession)
                && retiredBySession.TryGetValue(sessionId, out var retiredIds)
                && retiredIds.Contains(playSessionId);
        }

        private sealed class ProgressAdoptionCandidate
        {
            public string PlaySessionId { get; set; }
            public DateTime FirstObservedAt { get; set; }
            public DateTime LastObservedAt { get; set; }
            public long LastPositionTicks { get; set; }
            public int ObservationCount { get; set; }
        }

        private static PartyParticipant Clone(PartyParticipant participant)
        {
            if (participant == null)
            {
                return null;
            }

            return new PartyParticipant
            {
                UserId = participant.UserId,
                UserName = participant.UserName,
                SessionId = participant.SessionId,
                PlaySessionId = participant.PlaySessionId,
                JoinedAt = participant.JoinedAt,
                LastActivityAt = participant.LastActivityAt,
                CurrentPositionTicks = participant.CurrentPositionTicks,
                IsPaused = participant.IsPaused,
                IsBuffering = participant.IsBuffering
            };
        }
    }
}
