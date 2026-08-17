using System;
using System.Collections.Generic;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Identifies one pause-state command expectation so a failed command can revoke
    /// only the echo it created, even when newer commands target the same session.
    /// </summary>
    public readonly struct PauseStateExpectationToken
    {
        internal PauseStateExpectationToken(long value)
        {
            Value = value;
        }

        internal long Value { get; }

        public bool IsEmpty => Value == 0;
    }

    /// <summary>
    /// Immutable classification of one inbound playback-state report. Callers can retain
    /// this snapshot while later outbound commands create new echo expectations.
    /// </summary>
    public readonly struct InboundPauseStateClassification
    {
        internal InboundPauseStateClassification(
            bool isTransition,
            bool isExpectedCommandEcho,
            bool isSeekCommandEcho)
        {
            IsTransition = isTransition;
            IsExpectedCommandEcho = isExpectedCommandEcho;
            IsSeekCommandEcho = isSeekCommandEcho;
        }

        public bool IsTransition { get; }

        public bool IsExpectedCommandEcho { get; }

        public bool IsSeekCommandEcho { get; }

        // A seek marker is diagnostic only: unlike an explicit Pause/Unpause command,
        // it cannot prove that a state transition was synthetic. Suppressing it would
        // also suppress a real position-less user action during the same window.
        public bool IsSyntheticEcho => IsExpectedCommandEcho;
    }

    public sealed class PlaybackSyncCoordinator
    {
        // The settle window doubles as the quiet period after any commanded seek.
        // Slow sources (e.g. 115-backed streams) can take 10-30s to restart after a
        // seek; re-commanding during that window just restarts the buffering loop.
        private static readonly TimeSpan SeekCooldown = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan SeekConfirmTolerance = TimeSpan.FromSeconds(1.5);
        // Production traces show iOS emitting seek-induced pause transitions as late as
        // ~6.3s after the command while a 115 stream re-buffers. Eight seconds covers that
        // observed tail without suppressing intentional pause input for the full 30s.
        private static readonly TimeSpan SeekStateEchoWindow = TimeSpan.FromSeconds(8);
        // Pause is sent immediately before an active seek. On 115-backed playback the
        // iOS client has taken 9.37s to report the commanded paused state, so this echo
        // lifetime must cover the same slow-buffering envelope as seek settlement.
        private static readonly TimeSpan PauseEchoWindow = TimeSpan.FromSeconds(30);

        private readonly object _syncRoot = new object();
        private long _nextMasterClockRevision;
        private readonly Dictionary<string, PendingSeekState> _pendingSeeks = new Dictionary<string, PendingSeekState>();
        private readonly Dictionary<string, List<ExpectedPauseState>> _expectedPauseStates =
            new Dictionary<string, List<ExpectedPauseState>>();
        private readonly Dictionary<string, MasterClockState> _masterClocks = new Dictionary<string, MasterClockState>();
        private long _nextPauseExpectationId;

        /// <summary>
        /// Begins (or, for <paramref name="allowReplace"/>, replaces) a pending seek.
        /// Periodic corrections must not replace a still-settling master seek; only a new
        /// master-triggered seek may do so. The settle window throttles how often the same
        /// participant can be commanded, so periodic calibration never stacks seeks.
        /// <paramref name="force"/> is reserved for an accepted pause, where one explicit
        /// final-position command is required even when the same target is already pending.
        /// </summary>
        public bool TryBeginSeek(
            string sessionId,
            long targetPositionTicks,
            DateTime nowUtc,
            bool allowReplace = false,
            bool force = false)
        {
            if (string.IsNullOrEmpty(sessionId) || targetPositionTicks < 0)
            {
                return false;
            }

            lock (_syncRoot)
            {
                if (_pendingSeeks.TryGetValue(sessionId, out var pending))
                {
                    if (nowUtc >= pending.ExpiresAt)
                    {
                        // The settle window expired without client confirmation; allow a retry.
                        _pendingSeeks.Remove(sessionId);
                    }
                    else if (pending.TargetPositionTicks == targetPositionTicks && !force)
                    {
                        // Same target is still pending: suppress the duplicate.
                        return false;
                    }
                    else if (!allowReplace && !force)
                    {
                        // A periodic correction must not stomp a master seek that is still
                        // settling; otherwise the client gets bounced around by converging
                        // targets in quick succession.
                        return false;
                    }
                    else
                    {
                        // The master kept dragging: replace the pending target so the
                        // newer position is sent instead of being swallowed by cooldown.
                        pending.TargetPositionTicks = targetPositionTicks;
                        pending.CommandedAt = nowUtc;
                        pending.ExpiresAt = nowUtc + SeekCooldown;
                        pending.HasConfirmedTarget = false;
                        return true;
                    }
                }

                _pendingSeeks[sessionId] = new PendingSeekState
                {
                    TargetPositionTicks = targetPositionTicks,
                    CommandedAt = nowUtc,
                    ExpiresAt = nowUtc + SeekCooldown
                };
                return true;
            }
        }

        /// <summary>
        /// Cancels a seek command that failed before the server accepted it. The target
        /// must still match so a delayed failure from an older command cannot remove a
        /// newer replacement seek for the same Emby session.
        /// </summary>
        public bool CancelPendingSeek(string sessionId, long targetPositionTicks)
        {
            if (string.IsNullOrEmpty(sessionId) || targetPositionTicks < 0)
            {
                return false;
            }

            lock (_syncRoot)
            {
                if (!_pendingSeeks.TryGetValue(sessionId, out var pending)
                    || pending.TargetPositionTicks != targetPositionTicks)
                {
                    return false;
                }

                _pendingSeeks.Remove(sessionId);
                return true;
            }
        }

        /// <summary>
        /// Marks a pending seek as confirmed once the client reports a position at or near
        /// the requested target. Confirmation deliberately does not end the quiet period:
        /// Emby iOS can briefly echo the commanded UI position before its native player
        /// snaps back to the old position. Keeping the pending state prevents periodic
        /// calibration from turning that transient echo into a seek loop.
        /// </summary>
        public bool ConfirmSeekTarget(string sessionId, long positionTicks, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return false;
            }

            lock (_syncRoot)
            {
                if (!_pendingSeeks.TryGetValue(sessionId, out var pending))
                {
                    return false;
                }

                if (nowUtc >= pending.ExpiresAt)
                {
                    _pendingSeeks.Remove(sessionId);
                    return false;
                }

                if (Math.Abs(positionTicks - pending.TargetPositionTicks) <= SeekConfirmTolerance.Ticks)
                {
                    if (!pending.HasConfirmedTarget)
                    {
                        pending.HasConfirmedTarget = true;
                        return true;
                    }
                }

                return false;
            }
        }

        public bool IsSeekSettling(string sessionId, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return false;
            }

            lock (_syncRoot)
            {
                if (!_pendingSeeks.TryGetValue(sessionId, out var pending))
                {
                    return false;
                }

                if (nowUtc < pending.ExpiresAt)
                {
                    return true;
                }

                _pendingSeeks.Remove(sessionId);
                return false;
            }
        }

        /// <summary>
        /// Clears command state inherited from a previous playback instance that reused
        /// the same Emby SessionId. A duplicate start for the same PlaySessionId keeps the
        /// current cooldown so repeated PlaybackStart events cannot create a seek loop.
        /// </summary>
        public bool ResetForNewPlayback(
            string sessionId,
            string previousPlaySessionId,
            string newPlaySessionId)
        {
            if (string.IsNullOrEmpty(sessionId)
                || string.IsNullOrEmpty(newPlaySessionId)
                || string.Equals(
                    previousPlaySessionId,
                    newPlaySessionId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            ClearSession(sessionId);
            return true;
        }

        /// <summary>
        /// Returns true only during the short interval in which a seek may make a client
        /// emit synthetic pause/unpause transitions. This is intentionally much shorter
        /// than the buffering cooldown so real user pause input is not ignored for 30s.
        /// </summary>
        public bool IsSeekStateEchoExpected(string sessionId, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return false;
            }

            lock (_syncRoot)
            {
                return _pendingSeeks.TryGetValue(sessionId, out var pending)
                    && nowUtc >= pending.CommandedAt
                    && nowUtc < pending.CommandedAt + SeekStateEchoWindow;
            }
        }

        public PauseStateExpectationToken ExpectPauseState(
            string sessionId,
            bool isPaused,
            DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return default;
            }

            lock (_syncRoot)
            {
                if (!_expectedPauseStates.TryGetValue(sessionId, out var expectedStates))
                {
                    expectedStates = new List<ExpectedPauseState>();
                    _expectedPauseStates[sessionId] = expectedStates;
                }

                expectedStates.RemoveAll(expected => nowUtc >= expected.ExpiresAt);
                var expiresAt = nowUtc + PauseEchoWindow;
                var token = new PauseStateExpectationToken(NextPauseExpectationId());
                if (expectedStates.Count > 0
                    && expectedStates[expectedStates.Count - 1].IsPaused == isPaused)
                {
                    // Refresh a duplicate command instead of creating an expectation that
                    // could consume a later intentional state transition twice.
                    expectedStates[expectedStates.Count - 1].ExpiresAt = expiresAt;
                    expectedStates[expectedStates.Count - 1].TokenIds.Add(token.Value);
                }
                else
                {
                    var expectedState = new ExpectedPauseState
                    {
                        IsPaused = isPaused,
                        ExpiresAt = expiresAt
                    };
                    expectedState.TokenIds.Add(token.Value);
                    expectedStates.Add(expectedState);
                }

                return token;
            }
        }

        /// <summary>
        /// Revokes one command's expected echo. Other commands, including a newer
        /// command for the same session and state, remain eligible for consumption.
        /// </summary>
        public bool CancelExpectedPauseState(
            string sessionId,
            PauseStateExpectationToken token)
        {
            if (string.IsNullOrEmpty(sessionId) || token.IsEmpty)
            {
                return false;
            }

            lock (_syncRoot)
            {
                if (!_expectedPauseStates.TryGetValue(sessionId, out var expectedStates))
                {
                    return false;
                }

                for (var index = 0; index < expectedStates.Count; index++)
                {
                    var expected = expectedStates[index];
                    if (!expected.TokenIds.Remove(token.Value))
                    {
                        continue;
                    }

                    if (expected.TokenIds.Count == 0)
                    {
                        expectedStates.RemoveAt(index);
                    }

                    if (expectedStates.Count == 0)
                    {
                        _expectedPauseStates.Remove(sessionId);
                    }

                    return true;
                }

                return false;
            }
        }

        public bool ConsumeExpectedPauseState(
            string sessionId,
            bool isPaused,
            DateTime nowUtc,
            bool clearOnMismatch = false)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return false;
            }

            lock (_syncRoot)
            {
                return ConsumeExpectedPauseStateCore(
                    sessionId,
                    isPaused,
                    nowUtc,
                    clearOnMismatch);
            }
        }

        /// <summary>
        /// Classifies an inbound state report against the expectations that existed when
        /// the report arrived. This must be called before any rejoin/calibration command;
        /// the returned snapshot cannot be changed by expectations those commands add.
        /// </summary>
        public InboundPauseStateClassification ClassifyInboundPauseState(
            string sessionId,
            bool previousIsPaused,
            bool reportedIsPaused,
            DateTime nowUtc)
        {
            return ClassifyInboundPauseState(
                sessionId,
                previousIsPaused,
                reportedIsPaused,
                reportedPositionTicks: null,
                nowUtc);
        }

        /// <summary>
        /// A position-less transition inside the short seek window is marked as a
        /// possible player-reload echo for diagnostics. It is not treated as a proven
        /// synthetic command: viewers may also issue legitimate position-less pause or
        /// unpause actions during the same window.
        /// </summary>
        public InboundPauseStateClassification ClassifyInboundPauseState(
            string sessionId,
            bool previousIsPaused,
            bool reportedIsPaused,
            long? reportedPositionTicks,
            DateTime nowUtc)
        {
            var isTransition = reportedIsPaused != previousIsPaused;
            if (string.IsNullOrEmpty(sessionId))
            {
                return new InboundPauseStateClassification(
                    isTransition,
                    isExpectedCommandEcho: false,
                    isSeekCommandEcho: false);
            }

            lock (_syncRoot)
            {
                var isExpectedCommandEcho = ConsumeExpectedPauseStateCore(
                    sessionId,
                    reportedIsPaused,
                    nowUtc,
                    clearOnMismatch: isTransition);
                var isSeekCommandEcho = !reportedPositionTicks.HasValue
                    && _pendingSeeks.TryGetValue(sessionId, out var pending)
                    && nowUtc >= pending.CommandedAt
                    && nowUtc < pending.CommandedAt + SeekStateEchoWindow;

                return new InboundPauseStateClassification(
                    isTransition,
                    isExpectedCommandEcho,
                    isSeekCommandEcho);
            }
        }

        public bool UpdateMasterPosition(
            string partyId,
            long positionTicks,
            bool isPlaying,
            DateTime nowUtc,
            long seekThresholdTicks)
        {
            if (string.IsNullOrEmpty(partyId))
            {
                return false;
            }

            positionTicks = Math.Max(0, positionTicks);
            lock (_syncRoot)
            {
                var isSeek = false;
                if (_masterClocks.TryGetValue(partyId, out var previous))
                {
                    var expectedPosition = EstimatePosition(previous, nowUtc);
                    isSeek = Math.Abs(positionTicks - expectedPosition) > Math.Max(0, seekThresholdTicks);
                }

                _masterClocks[partyId] = new MasterClockState
                {
                    PositionTicks = positionTicks,
                    IsPlaying = isPlaying,
                    UpdatedAt = nowUtc,
                    Revision = NextMasterClockRevision()
                };
                return isSeek;
            }
        }

        /// <summary>
        /// Updates the authoritative master clock while leaving a drastic near-zero
        /// report staged for caller-side debounce. Emby Web can emit a synthetic ~1s
        /// position while its video element is being recreated; committing that sample
        /// makes the following real position look like another seek and causes a command
        /// on every reload cycle.
        /// </summary>
        public MasterPositionUpdateResult UpdateMasterPositionGuardingReloadArtifact(
            string partyId,
            long positionTicks,
            bool isPlaying,
            DateTime nowUtc,
            long seekThresholdTicks,
            long nearZeroThresholdTicks,
            long priorPositionThresholdTicks)
        {
            if (string.IsNullOrEmpty(partyId))
            {
                return new MasterPositionUpdateResult(
                    MasterPositionUpdateKind.Continuous,
                    Math.Max(0, positionTicks),
                    authoritativeRevision: 0);
            }

            positionTicks = Math.Max(0, positionTicks);
            nearZeroThresholdTicks = Math.Max(0, nearZeroThresholdTicks);
            priorPositionThresholdTicks = Math.Max(0, priorPositionThresholdTicks);

            lock (_syncRoot)
            {
                var hasPrevious = _masterClocks.TryGetValue(partyId, out var previous);
                var expectedPosition = hasPrevious
                    ? EstimatePosition(previous, nowUtc)
                    : positionTicks;
                var isDeferredReloadArtifact = hasPrevious
                    && positionTicks <= nearZeroThresholdTicks
                    && expectedPosition > priorPositionThresholdTicks;

                if (isDeferredReloadArtifact)
                {
                    return new MasterPositionUpdateResult(
                        MasterPositionUpdateKind.DeferredReloadArtifact,
                        expectedPosition,
                        previous.Revision);
                }

                var isSeek = hasPrevious
                    && Math.Abs(positionTicks - expectedPosition) > Math.Max(0, seekThresholdTicks);
                _masterClocks[partyId] = new MasterClockState
                {
                    PositionTicks = positionTicks,
                    IsPlaying = isPlaying,
                    UpdatedAt = nowUtc,
                    Revision = NextMasterClockRevision()
                };

                return new MasterPositionUpdateResult(
                    isSeek ? MasterPositionUpdateKind.Seek : MasterPositionUpdateKind.Continuous,
                    positionTicks,
                    _masterClocks[partyId].Revision);
            }
        }

        /// <summary>
        /// Commits a deferred near-zero report only if no newer authoritative master
        /// update arrived while the caller's debounce window was open.
        /// </summary>
        public bool TryCommitDeferredMasterPosition(
            string partyId,
            long expectedAuthoritativeRevision,
            long deferredPositionTicks,
            long stagedAuthoritativePositionTicks,
            DateTime nowUtc,
            out long committedPositionTicks)
        {
            committedPositionTicks = Math.Max(0, deferredPositionTicks);
            if (string.IsNullOrEmpty(partyId))
            {
                return false;
            }

            lock (_syncRoot)
            {
                if (!_masterClocks.TryGetValue(partyId, out var current)
                    || current.Revision != expectedAuthoritativeRevision)
                {
                    return false;
                }

                var currentAuthoritativePosition = EstimatePosition(current, nowUtc);
                var playedSinceStaging = Math.Max(
                    0,
                    currentAuthoritativePosition - Math.Max(0, stagedAuthoritativePositionTicks));
                committedPositionTicks = Math.Max(0, deferredPositionTicks) + playedSinceStaging;
                _masterClocks[partyId] = new MasterClockState
                {
                    PositionTicks = committedPositionTicks,
                    IsPlaying = current.IsPlaying,
                    UpdatedAt = nowUtc,
                    Revision = NextMasterClockRevision()
                };
                return true;
            }
        }

        public long GetEstimatedPartyPosition(string partyId, long fallbackPositionTicks, DateTime nowUtc)
        {
            lock (_syncRoot)
            {
                return !string.IsNullOrEmpty(partyId) && _masterClocks.TryGetValue(partyId, out var clock)
                    ? EstimatePosition(clock, nowUtc)
                    : Math.Max(0, fallbackPositionTicks);
            }
        }

        /// <summary>
        /// Changes only the running/paused state of the master clock while preserving its
        /// projected position. Emby Web frequently emits position-less StateChange events;
        /// a position-less Pause must still freeze the clock and a position-less Unpause
        /// must resume it from that frozen point.
        /// </summary>
        public long SetMasterPlaybackState(
            string partyId,
            bool isPlaying,
            long fallbackPositionTicks,
            DateTime nowUtc)
        {
            fallbackPositionTicks = Math.Max(0, fallbackPositionTicks);
            if (string.IsNullOrEmpty(partyId))
            {
                return fallbackPositionTicks;
            }

            lock (_syncRoot)
            {
                var hasClock = _masterClocks.TryGetValue(partyId, out var clock);
                var preservedPosition = hasClock
                    ? EstimatePosition(clock, nowUtc)
                    : fallbackPositionTicks;
                _masterClocks[partyId] = new MasterClockState
                {
                    PositionTicks = preservedPosition,
                    IsPlaying = isPlaying,
                    UpdatedAt = nowUtc,
                    // A position-less state report changes pause/play projection but
                    // is not a newer positional sample. Keep the position revision so
                    // a genuine deferred seek to the beginning can still be confirmed.
                    Revision = hasClock ? clock.Revision : NextMasterClockRevision()
                };
                return preservedPosition;
            }
        }

        public void StopMasterClock(string partyId, long positionTicks, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(partyId))
            {
                return;
            }

            lock (_syncRoot)
            {
                _masterClocks[partyId] = new MasterClockState
                {
                    PositionTicks = Math.Max(0, positionTicks),
                    IsPlaying = false,
                    UpdatedAt = nowUtc,
                    Revision = NextMasterClockRevision()
                };
            }
        }

        public void ClearSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return;
            }

            lock (_syncRoot)
            {
                _pendingSeeks.Remove(sessionId);
                _expectedPauseStates.Remove(sessionId);
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
                _masterClocks.Remove(partyId);
            }
        }

        private long NextPauseExpectationId()
        {
            if (_nextPauseExpectationId == long.MaxValue)
            {
                _nextPauseExpectationId = 0;
            }

            return ++_nextPauseExpectationId;
        }

        private long NextMasterClockRevision()
        {
            if (_nextMasterClockRevision == long.MaxValue)
            {
                _nextMasterClockRevision = 0;
            }

            return ++_nextMasterClockRevision;
        }

        private bool ConsumeExpectedPauseStateCore(
            string sessionId,
            bool isPaused,
            DateTime nowUtc,
            bool clearOnMismatch)
        {
            if (!_expectedPauseStates.TryGetValue(sessionId, out var expectedStates))
            {
                return false;
            }

            expectedStates.RemoveAll(expected => nowUtc >= expected.ExpiresAt);
            if (expectedStates.Count == 0)
            {
                _expectedPauseStates.Remove(sessionId);
                return false;
            }

            var matchingIndex = expectedStates.FindIndex(expected => expected.IsPaused == isPaused);
            if (matchingIndex < 0)
            {
                if (clearOnMismatch)
                {
                    // A genuine transition to a state we never commanded is user/client
                    // input. Invalidate the queue so it cannot swallow a later action.
                    _expectedPauseStates.Remove(sessionId);
                }
                return false;
            }

            // Native players may acknowledge Pause and Unpause commands out of order
            // while a stream is settling. Remove only the matching command so every
            // other outstanding echo can still be recognized when it arrives.
            expectedStates.RemoveAt(matchingIndex);
            if (expectedStates.Count == 0)
            {
                _expectedPauseStates.Remove(sessionId);
            }
            return true;
        }

        private static long EstimatePosition(MasterClockState clock, DateTime nowUtc)
        {
            if (!clock.IsPlaying || nowUtc <= clock.UpdatedAt)
            {
                return clock.PositionTicks;
            }

            return clock.PositionTicks + (nowUtc - clock.UpdatedAt).Ticks;
        }

        private sealed class ExpectedPauseState
        {
            public bool IsPaused { get; set; }
            public DateTime ExpiresAt { get; set; }
            public HashSet<long> TokenIds { get; } = new HashSet<long>();
        }

        private sealed class PendingSeekState
        {
            public long TargetPositionTicks { get; set; }
            public DateTime CommandedAt { get; set; }
            public DateTime ExpiresAt { get; set; }
            public bool HasConfirmedTarget { get; set; }
        }

        private sealed class MasterClockState
        {
            public long PositionTicks { get; set; }
            public bool IsPlaying { get; set; }
            public DateTime UpdatedAt { get; set; }
            public long Revision { get; set; }
        }
    }

    public enum MasterPositionUpdateKind
    {
        Continuous,
        Seek,
        DeferredReloadArtifact
    }

    public readonly struct MasterPositionUpdateResult
    {
        public MasterPositionUpdateResult(
            MasterPositionUpdateKind kind,
            long authoritativePositionTicks,
            long authoritativeRevision)
        {
            Kind = kind;
            AuthoritativePositionTicks = authoritativePositionTicks;
            AuthoritativeRevision = authoritativeRevision;
        }

        public MasterPositionUpdateKind Kind { get; }
        public bool IsSeek => Kind == MasterPositionUpdateKind.Seek;
        public bool IsDeferredReloadArtifact => Kind == MasterPositionUpdateKind.DeferredReloadArtifact;
        public long AuthoritativePositionTicks { get; }
        public long AuthoritativeRevision { get; }
    }
}
