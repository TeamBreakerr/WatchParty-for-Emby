using System;
using System.Collections.Generic;

namespace WatchPartyForEmby
{
    public enum PauseControlMode
    {
        Anyone,
        Host,
        Vote
    }

    public static class PauseControlModeParser
    {
        public static PauseControlMode Parse(string value)
        {
            TryParse(value, out var mode);
            return mode;
        }

        public static bool TryParse(string value, out PauseControlMode mode)
        {
            var normalized = value?.Trim();
            if (string.Equals(normalized, "Anyone", StringComparison.OrdinalIgnoreCase))
            {
                mode = PauseControlMode.Anyone;
                return true;
            }

            if (string.Equals(normalized, "Host", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "HostOnly", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "Master", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "MasterOnly", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "Disabled", StringComparison.OrdinalIgnoreCase))
            {
                // Older versions used several different labels for the same safe policy:
                // only the controlling host may change pause state.
                mode = PauseControlMode.Host;
                return true;
            }

            if (string.Equals(normalized, "Vote", StringComparison.OrdinalIgnoreCase))
            {
                mode = PauseControlMode.Vote;
                return true;
            }

            // Preserve the historical/default configuration behavior for missing or
            // unrecognized values while still making the failed parse observable.
            mode = PauseControlMode.Anyone;
            return false;
        }
    }

    public enum PauseControlDecisionKind
    {
        Broadcast,
        RejectActor,
        WaitForVotes
    }

    public sealed class PauseControlDecision
    {
        internal PauseControlDecision(
            PauseControlDecisionKind kind,
            bool authoritativeIsPaused,
            int voteCount,
            int requiredVotes,
            bool isDuplicateVote)
        {
            Kind = kind;
            AuthoritativeIsPaused = authoritativeIsPaused;
            VoteCount = voteCount;
            RequiredVotes = requiredVotes;
            IsDuplicateVote = isDuplicateVote;
        }

        public PauseControlDecisionKind Kind { get; }

        /// <summary>
        /// The state that should be applied by the caller. For Broadcast it is sent to
        /// the room; for RejectActor and WaitForVotes it is sent only to the actor so a
        /// local state change cannot outrun the room policy.
        /// </summary>
        public bool AuthoritativeIsPaused { get; }

        public int VoteCount { get; }

        public int RequiredVotes { get; }

        public bool IsDuplicateVote { get; }
    }

    /// <summary>
    /// Owns the pause-policy state for all active watch parties. The coordinator is pure
    /// in-memory policy: it decides what should happen but never sends Emby commands.
    /// Every public operation is serialized through one lock so duplicate or concurrent
    /// playback reports cannot count a user more than once or race a party reset.
    /// </summary>
    public sealed class PauseControlCoordinator
    {
        private readonly object _syncRoot = new object();
        private readonly Dictionary<string, PartyPauseControlState> _parties =
            new Dictionary<string, PartyPauseControlState>(StringComparer.Ordinal);

        /// <summary>
        /// Initializes or replaces a party's authoritative pause state and clears any
        /// votes left from an earlier playback instance.
        /// </summary>
        public void ResetParty(string partyId, bool isPaused)
        {
            EnsureIdentifier(partyId, nameof(partyId));

            lock (_syncRoot)
            {
                _parties[partyId] = new PartyPauseControlState
                {
                    IsPaused = isPaused
                };
            }
        }

        /// <summary>
        /// Reconciles policy state with playback reported by the room's authoritative
        /// master. Repeated observations of the same state preserve pending votes;
        /// an actual authoritative transition invalidates votes for the old state.
        /// </summary>
        public bool ObserveAuthoritativeState(string partyId, bool isPaused)
        {
            EnsureIdentifier(partyId, nameof(partyId));

            lock (_syncRoot)
            {
                if (!_parties.TryGetValue(partyId, out var state))
                {
                    _parties.Add(
                        partyId,
                        new PartyPauseControlState
                        {
                            IsPaused = isPaused
                        });
                    return true;
                }

                if (state.IsPaused == isPaused)
                {
                    return false;
                }

                state.IsPaused = isPaused;
                state.Voters.Clear();
                return true;
            }
        }

        /// <summary>
        /// Evaluates one user-originated pause or unpause request. Parties that have not
        /// been explicitly reset start in the playing state, matching WatchPartyItem's
        /// default. Vote mode requires a strict majority of distinct users for both
        /// transitions; until it passes, the caller should restore only the actor to
        /// <see cref="PauseControlDecision.AuthoritativeIsPaused"/>.
        /// </summary>
        public PauseControlDecision HandleRequest(
            string partyId,
            PauseControlMode mode,
            string actorUserId,
            bool actorIsHost,
            bool requestedIsPaused,
            int activeParticipantCount)
        {
            EnsureIdentifier(partyId, nameof(partyId));
            EnsureIdentifier(actorUserId, nameof(actorUserId));
            if (!Enum.IsDefined(typeof(PauseControlMode), mode))
            {
                throw new ArgumentOutOfRangeException(nameof(mode));
            }

            if (activeParticipantCount <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(activeParticipantCount),
                    "An active party must contain at least one participant.");
            }

            lock (_syncRoot)
            {
                if (!_parties.TryGetValue(partyId, out var state))
                {
                    state = new PartyPauseControlState();
                    _parties.Add(partyId, state);
                }

                if (state.Mode.HasValue && state.Mode.Value != mode)
                {
                    // Votes belong to one configured policy and must not leak across a
                    // live configuration change.
                    state.Voters.Clear();
                }

                state.Mode = mode;

                switch (mode)
                {
                    case PauseControlMode.Anyone:
                        return Broadcast(state, requestedIsPaused);

                    case PauseControlMode.Host:
                        return actorIsHost
                            ? Broadcast(state, requestedIsPaused)
                            : RejectActor(state, voteCount: 0, requiredVotes: 0);

                    case PauseControlMode.Vote:
                        return HandleVote(
                            state,
                            actorUserId,
                            requestedIsPaused,
                            activeParticipantCount);

                    default:
                        throw new ArgumentOutOfRangeException(nameof(mode));
                }
            }
        }

        /// <summary>
        /// Removes a departed distinct user's pending vote without disturbing votes from
        /// other members. Callers with multiple sessions for one user should call this
        /// only after that user's last party session has left.
        /// </summary>
        public bool RemoveMember(string partyId, string userId)
        {
            EnsureIdentifier(partyId, nameof(partyId));
            EnsureIdentifier(userId, nameof(userId));

            lock (_syncRoot)
            {
                return _parties.TryGetValue(partyId, out var state)
                    && state.Voters.Remove(userId);
            }
        }

        public bool ClearParty(string partyId)
        {
            EnsureIdentifier(partyId, nameof(partyId));

            lock (_syncRoot)
            {
                return _parties.Remove(partyId);
            }
        }

        private static PauseControlDecision HandleVote(
            PartyPauseControlState state,
            string actorUserId,
            bool requestedIsPaused,
            int activeParticipantCount)
        {
            var requiredVotes = (activeParticipantCount / 2) + 1;

            if (requestedIsPaused == state.IsPaused)
            {
                // A user returning to the authoritative state withdraws only their own
                // pending vote. In particular, one unpause report never clears the room's
                // entire vote set.
                state.Voters.Remove(actorUserId);
                return RejectActor(state, state.Voters.Count, requiredVotes);
            }

            var added = state.Voters.Add(actorUserId);
            var voteCount = state.Voters.Count;
            if (voteCount < requiredVotes)
            {
                return new PauseControlDecision(
                    PauseControlDecisionKind.WaitForVotes,
                    state.IsPaused,
                    voteCount,
                    requiredVotes,
                    isDuplicateVote: !added);
            }

            state.IsPaused = requestedIsPaused;
            state.Voters.Clear();
            return new PauseControlDecision(
                PauseControlDecisionKind.Broadcast,
                requestedIsPaused,
                voteCount,
                requiredVotes,
                isDuplicateVote: !added);
        }

        private static PauseControlDecision Broadcast(
            PartyPauseControlState state,
            bool requestedIsPaused)
        {
            state.IsPaused = requestedIsPaused;
            state.Voters.Clear();
            return new PauseControlDecision(
                PauseControlDecisionKind.Broadcast,
                requestedIsPaused,
                voteCount: 0,
                requiredVotes: 0,
                isDuplicateVote: false);
        }

        private static PauseControlDecision RejectActor(
            PartyPauseControlState state,
            int voteCount,
            int requiredVotes)
        {
            return new PauseControlDecision(
                PauseControlDecisionKind.RejectActor,
                state.IsPaused,
                voteCount,
                requiredVotes,
                isDuplicateVote: false);
        }

        private static void EnsureIdentifier(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("A non-empty identifier is required.", parameterName);
            }
        }

        private sealed class PartyPauseControlState
        {
            public bool IsPaused { get; set; }

            public PauseControlMode? Mode { get; set; }

            public HashSet<string> Voters { get; } =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
