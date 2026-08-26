using System;
using System.Collections.Concurrent;

namespace WatchPartyForEmby
{
    public sealed class MasterSessionAssignment
    {
        public bool Registered { get; set; }
        public bool AuthorityChanged { get; set; }
        public string PreviousMasterSessionId { get; set; }
        public string CurrentMasterSessionId { get; set; }
    }

    public sealed class MasterSessionResolution
    {
        public bool IsMaster { get; set; }
        public bool AuthorityChanged { get; set; }
        public string PreviousMasterSessionId { get; set; }
    }

    /// <summary>
    /// Owns the per-party boundary for master assignment, playback-generation
    /// validation and departure effects. Session event queues are keyed by Emby
    /// SessionId, so two candidate master sessions can otherwise race even though
    /// each individual session is processed in order.
    /// </summary>
    public sealed class MasterSessionLifecycleCoordinator
    {
        private readonly ConcurrentDictionary<string, object> _partyGates =
            new ConcurrentDictionary<string, object>(StringComparer.Ordinal);
        private readonly PartySessionRegistry _sessions;
        private readonly ParticipantDormancyTracker _dormancies;

        public MasterSessionLifecycleCoordinator(
            PartySessionRegistry sessions,
            ParticipantDormancyTracker dormancies)
        {
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _dormancies = dormancies ?? throw new ArgumentNullException(nameof(dormancies));
        }

        public void Execute(string partyId, Action operation)
        {
            if (string.IsNullOrEmpty(partyId))
            {
                throw new ArgumentException("partyId is required", nameof(partyId));
            }
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            var gate = _partyGates.GetOrAdd(partyId, _ => new object());
            lock (gate)
            {
                operation();
            }
        }

        /// <summary>
        /// Changes the authoritative session as one lifecycle operation. Work owned by
        /// the prior authority is retired before the replacement becomes visible, and
        /// the new master cannot remain dormant.
        /// </summary>
        public MasterSessionAssignment TryAssignMaster(
            string partyId,
            string sessionId,
            bool replaceExisting,
            Action<string> retirePreviousAuthority)
        {
            MasterSessionAssignment result = null;
            Execute(
                partyId,
                () => result = TryAssignMasterWithinBoundary(
                    partyId,
                    sessionId,
                    replaceExisting,
                    retirePreviousAuthority));

            return result;
        }

        internal MasterSessionAssignment TryAssignMasterWithinBoundary(
            string partyId,
            string sessionId,
            bool replaceExisting,
            Action<string> retirePreviousAuthority)
        {
            var result = new MasterSessionAssignment
            {
                CurrentMasterSessionId = sessionId
            };
            if (string.IsNullOrEmpty(sessionId)
                || !_sessions.TryGetSession(partyId, sessionId, out _))
            {
                return result;
            }

            var previousMasterSessionId = _sessions.GetMasterSession(partyId);
            result.PreviousMasterSessionId = previousMasterSessionId;
            if (string.Equals(
                    previousMasterSessionId,
                    sessionId,
                    StringComparison.Ordinal))
            {
                _dormancies.Cancel(partyId, sessionId);
                result.Registered = true;
                return result;
            }

            if (!replaceExisting && !string.IsNullOrEmpty(previousMasterSessionId))
            {
                return result;
            }

            // Candidate membership and every production playback-generation
            // mutation use this same party gate. Retiring the old command scope
            // before SetMasterSession therefore cannot strand the party without
            // authority because of an intervening removal.
            retirePreviousAuthority?.Invoke(previousMasterSessionId);
            var registered = replaceExisting
                ? _sessions.SetMasterSession(partyId, sessionId)
                : _sessions.SetMasterSessionIfAbsent(partyId, sessionId);
            if (!registered)
            {
                return result;
            }

            _dormancies.Cancel(partyId, sessionId);
            result.Registered = true;
            result.AuthorityChanged = true;
            return result;
        }

        /// <summary>
        /// Commits an authoritative side effect only while both the master SessionId
        /// and PlaySessionId still match. The validation and mutation share the same
        /// party gate as assignment, Stop resolution and generation replacement.
        /// </summary>
        public bool TryExecuteCurrentMasterPlayback(
            string partyId,
            string sessionId,
            string playSessionId,
            Action operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            var applied = false;
            Execute(
                partyId,
                () =>
                {
                    if (!_sessions.IsCurrentMasterPlayback(
                            partyId,
                            sessionId,
                            playSessionId))
                    {
                        return;
                    }

                    operation();
                    applied = true;
                });
            return applied;
        }
    }
}
