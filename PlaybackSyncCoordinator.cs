using System;
using System.Collections.Generic;
using System.Linq;

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
        private static readonly IReadOnlyList<PauseStateExpectationToken>
            EmptyMatchedExpectations = Array.Empty<PauseStateExpectationToken>();
        private readonly IReadOnlyList<PauseStateExpectationToken> _matchedExpectations;

        internal InboundPauseStateClassification(
            bool isTransition,
            bool isExpectedCommandEcho,
            bool isSeekCommandEcho,
            IReadOnlyList<PauseStateExpectationToken> matchedExpectations)
        {
            IsTransition = isTransition;
            IsExpectedCommandEcho = isExpectedCommandEcho;
            IsSeekCommandEcho = isSeekCommandEcho;
            _matchedExpectations = matchedExpectations;
        }

        public bool IsTransition { get; }

        public bool IsExpectedCommandEcho { get; }

        public bool IsSeekCommandEcho { get; }

        public IReadOnlyList<PauseStateExpectationToken> MatchedExpectations =>
            _matchedExpectations ?? EmptyMatchedExpectations;

        public PauseStateExpectationToken MatchedExpectation =>
            MatchedExpectations.Count == 1
                ? MatchedExpectations[0]
                : default;

        // A position-less transition inside the short seek window is treated as a
        // synthetic reload echo. This prevents a delayed buffer-induced Pause/Unpause
        // from becoming a new room-wide control event; intentional input is accepted
        // again as soon as the short window expires.
        public bool IsSyntheticEcho => IsExpectedCommandEcho || IsSeekCommandEcho;
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
        // Some native players acknowledge the commanded Pause and then emit one
        // opposite Unpause at the seek target while their video element is rebuilt.
        // That reversal is still command plumbing, not viewer input. Keep this window
        // deliberately short so a real pause/unpause remains responsive.
        private static readonly TimeSpan ReversePauseEchoWindow = TimeSpan.FromSeconds(3);
        // Web can emit an old position immediately after the near-zero sample produced
        // while rebuilding its video element. Keep the authoritative clock untouched
        // for this short handoff window; a later report is evaluated normally.
        private static readonly TimeSpan ReloadArtifactReportWindow =
            TimeSpan.FromMilliseconds(500);
        // Once the first delayed non-zero sample is identified as stale, Web can
        // continue sending that old element's 5-second heartbeat for a few seconds.
        // Keep the same logical seek generation open long enough to discard that
        // monotonic stale chain as well.
        private static readonly TimeSpan ReloadArtifactSequenceWindow =
            TimeSpan.FromSeconds(12);
        private static readonly TimeSpan ReloadArtifactProgressTolerance =
            TimeSpan.FromSeconds(2);
        // A clearly new, distant target must win immediately even if Web has just
        // emitted the near-zero reload sample. This keeps a quick 1s -> 100s drag
        // responsive instead of waiting for the stale-heartbeat window.
        private static readonly TimeSpan ImmediateMasterTargetThreshold =
            TimeSpan.FromSeconds(30);
        // Some third-party masters (notably Conflux after a player reload) keep
        // sending the exact position of the old playback instance while the clock
        // continues to advance. That report is not a second seek. Treat a position
        // that is effectively unchanged from the last accepted target as stale
        // heartbeat noise, otherwise every heartbeat re-queues a remote seek.
        private static readonly TimeSpan StationaryMasterReportTolerance =
            TimeSpan.FromSeconds(1);

        private readonly object _syncRoot = new object();
        private long _nextMasterClockRevision;
        private readonly Dictionary<string, PendingSeekState> _pendingSeeks = new Dictionary<string, PendingSeekState>();
        private readonly Dictionary<string, List<ExpectedPauseState>> _expectedPauseStates =
            new Dictionary<string, List<ExpectedPauseState>>();
        private readonly Dictionary<string, LastPauseEchoState> _lastPauseEchoStates =
            new Dictionary<string, LastPauseEchoState>();
        private readonly Dictionary<string, MasterClockState> _masterClocks = new Dictionary<string, MasterClockState>();
        private readonly Dictionary<string, ReloadArtifactState> _reloadArtifacts =
            new Dictionary<string, ReloadArtifactState>();
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
                        _lastPauseEchoStates.Remove(sessionId);
                        return true;
                    }
                }

                _lastPauseEchoStates.Remove(sessionId);
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

        /// <summary>
        /// Returns true when a participant report is still an old player position
        /// rather than an acknowledgement of the latest server seek. A controlled
        /// client may report its old timeline for several seconds while buffering;
        /// accepting that report would overwrite the participant checkpoint and make
        /// periodic calibration pull it back to the old position.
        /// </summary>
        public bool ShouldIgnorePositionReport(
            string sessionId,
            long positionTicks,
            DateTime nowUtc,
            out long expectedTargetTicks)
        {
            expectedTargetTicks = Math.Max(0, positionTicks);
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

                expectedTargetTicks = pending.TargetPositionTicks;
                return Math.Abs(Math.Max(0, positionTicks) - pending.TargetPositionTicks)
                    > SeekConfirmTolerance.Ticks;
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
        /// Returns whether a pause/play command still lacks a matching client state
        /// report. This lets the transport issue a small idempotent retry when a
        /// WebSocket frame is lost, while stopping immediately after the first echo.
        /// </summary>
        public bool IsPauseStateExpectationPending(
            string sessionId,
            PauseStateExpectationToken token,
            DateTime nowUtc)
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

                expectedStates.RemoveAll(expected => nowUtc >= expected.ExpiresAt);
                if (expectedStates.Count == 0)
                {
                    _expectedPauseStates.Remove(sessionId);
                    return false;
                }

                return expectedStates.Any(expected => expected.TokenIds.Contains(token.Value));
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

        /// <summary>
        /// Removes an expectation only when its command never reached the client. Once a
        /// Pause/Unpause was dispatched, a superseding master generation may stop retries
        /// but must retain this marker so a delayed client echo is still consumed.
        /// </summary>
        public bool CompletePauseStateCommandAttempt(
            string sessionId,
            PauseStateExpectationToken token,
            bool commandWasDispatched)
        {
            return !commandWasDispatched
                && CancelExpectedPauseState(sessionId, token);
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
                    clearOnMismatch,
                    out _);
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
        /// player-reload echo. The server suppresses it so a delayed buffer-induced
        /// Pause/Unpause cannot become a new room-wide control event.
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
                    isSeekCommandEcho: false,
                    matchedExpectations: Array.Empty<PauseStateExpectationToken>());
            }

            lock (_syncRoot)
            {
                var isExpectedCommandEcho = ConsumeExpectedPauseStateCore(
                    sessionId,
                    reportedIsPaused,
                    nowUtc,
                    clearOnMismatch: isTransition,
                    matchedExpectations: out var matchedExpectations);
                var isSeekCommandEcho = false;
                if (_pendingSeeks.TryGetValue(sessionId, out var pending)
                    && nowUtc >= pending.CommandedAt
                    && nowUtc < pending.CommandedAt + SeekStateEchoWindow)
                {
                    // A rebuffering player can report the old position together with
                    // the opposite pause state after a remote seek. Treat that as the
                    // same synthetic reload echo as a position-less report, but keep a
                    // positioned report at the commanded target eligible as real input.
                    isSeekCommandEcho = !reportedPositionTicks.HasValue
                        || Math.Abs(
                            Math.Max(0, reportedPositionTicks.Value)
                            - pending.TargetPositionTicks) > SeekConfirmTolerance.Ticks;

                    if (!isSeekCommandEcho
                        && isTransition
                        && _lastPauseEchoStates.TryGetValue(sessionId, out var lastEcho)
                        && lastEcho.IsPaused != reportedIsPaused
                        && nowUtc >= lastEcho.EchoedAt
                        && nowUtc < lastEcho.EchoedAt + ReversePauseEchoWindow)
                    {
                        // Pause(target) -> Unpause(target) is the characteristic iOS
                        // seek acknowledgement sequence. The first state consumed an
                        // expected command echo above; suppress only this immediate
                        // opposite transition, never an arbitrary target-position state.
                        isSeekCommandEcho = true;
                    }
                }

                return new InboundPauseStateClassification(
                    isTransition,
                    isExpectedCommandEcho,
                    isSeekCommandEcho,
                    matchedExpectations);
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
                _reloadArtifacts.Remove(partyId);
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
        /// Applies an explicit seek notification from a patched master client. Unlike a
        /// normal progress report, this is authoritative even when the target is near
        /// zero or far from the projected clock; the client explicitly told us that the
        /// user moved the timeline.
        /// </summary>
        public MasterPositionUpdateResult ApplyExplicitMasterSeek(
            string partyId,
            long positionTicks,
            bool isPlaying,
            DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(partyId))
            {
                return new MasterPositionUpdateResult(
                    MasterPositionUpdateKind.Seek,
                    Math.Max(0, positionTicks),
                    authoritativeRevision: 0);
            }

            positionTicks = Math.Max(0, positionTicks);
            lock (_syncRoot)
            {
                _reloadArtifacts.Remove(partyId);
                _masterClocks[partyId] = new MasterClockState
                {
                    PositionTicks = positionTicks,
                    IsPlaying = isPlaying,
                    UpdatedAt = nowUtc,
                    Revision = NextMasterClockRevision()
                };
                return new MasterPositionUpdateResult(
                    MasterPositionUpdateKind.Seek,
                    positionTicks,
                    _masterClocks[partyId].Revision);
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
            long priorPositionThresholdTicks,
            bool acceptInferredSeek = true)
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

                if (_reloadArtifacts.TryGetValue(partyId, out var reloadArtifact))
                {
                    if (!acceptInferredSeek)
                    {
                        _reloadArtifacts.Remove(partyId);
                        return new MasterPositionUpdateResult(
                            MasterPositionUpdateKind.IgnoredUnexpectedDiscontinuity,
                            expectedPosition,
                            reloadArtifact.AuthoritativeRevision);
                    }

                    if (positionTicks <= nearZeroThresholdTicks)
                    {
                        return new MasterPositionUpdateResult(
                            MasterPositionUpdateKind.DeferredReloadArtifact,
                            expectedPosition,
                            reloadArtifact.AuthoritativeRevision);
                    }

                    if (!reloadArtifact.HasStaleSample
                        && positionTicks >= ImmediateMasterTargetThreshold.Ticks)
                    {
                        _reloadArtifacts.Remove(partyId);
                    }
                    else if (!reloadArtifact.HasStaleSample
                        && nowUtc - reloadArtifact.FirstObservedAt < ReloadArtifactReportWindow)
                    {
                        reloadArtifact.HasStaleSample = true;
                        reloadArtifact.LastStalePositionTicks = positionTicks;
                        reloadArtifact.LastStaleReportAt = nowUtc;
                        return new MasterPositionUpdateResult(
                            MasterPositionUpdateKind.IgnoredReloadArtifactReport,
                            expectedPosition,
                            reloadArtifact.AuthoritativeRevision);
                    }

                    if (reloadArtifact.HasStaleSample
                        && nowUtc - reloadArtifact.FirstObservedAt < ReloadArtifactSequenceWindow)
                    {
                        var elapsedTicks = Math.Max(
                            0,
                            (nowUtc - reloadArtifact.LastStaleReportAt).Ticks);
                        var positionDeltaTicks = positionTicks - reloadArtifact.LastStalePositionTicks;
                        var expectedDeltaTicks = previous != null && previous.IsPlaying
                            ? elapsedTicks
                            : 0;
                        var isMonotonicStaleHeartbeat = positionDeltaTicks >= 0
                            && Math.Abs(positionDeltaTicks - expectedDeltaTicks)
                                <= ReloadArtifactProgressTolerance.Ticks;
                        if (isMonotonicStaleHeartbeat)
                        {
                            reloadArtifact.LastStalePositionTicks = positionTicks;
                            reloadArtifact.LastStaleReportAt = nowUtc;
                            return new MasterPositionUpdateResult(
                                MasterPositionUpdateKind.IgnoredReloadArtifactReport,
                                expectedPosition,
                                reloadArtifact.AuthoritativeRevision);
                        }

                        // A cached Web tab can interleave an older heartbeat after a
                        // newer one (for example 1s -> 19.9s -> 1s -> 29.9s). Small,
                        // non-monotonic samples in the same reload generation are still
                        // stale timeline noise. Keep the near-zero candidate alive;
                        // only a clearly distant target is allowed to break the
                        // generation immediately.
                        if (positionTicks < ImmediateMasterTargetThreshold.Ticks)
                        {
                            reloadArtifact.LastStalePositionTicks = positionTicks;
                            reloadArtifact.LastStaleReportAt = nowUtc;
                            return new MasterPositionUpdateResult(
                                MasterPositionUpdateKind.IgnoredReloadArtifactReport,
                                expectedPosition,
                                reloadArtifact.AuthoritativeRevision);
                        }
                    }

                    _reloadArtifacts.Remove(partyId);
                }

                // Once the caller has observed the dedicated Web seek endpoint it can
                // safely reject every discontinuous regular heartbeat as reload noise.
                // Before that first explicit notification, the caller may opt into the
                // hardened legacy near-zero inference path below for an old cached tab.
                var isDeferredReloadArtifact = acceptInferredSeek
                    && hasPrevious
                    && positionTicks <= nearZeroThresholdTicks
                    && expectedPosition > priorPositionThresholdTicks;

                if (isDeferredReloadArtifact)
                {
                    _reloadArtifacts[partyId] = new ReloadArtifactState
                    {
                        FirstObservedAt = nowUtc,
                        AuthoritativeRevision = previous.Revision
                    };
                    return new MasterPositionUpdateResult(
                        MasterPositionUpdateKind.DeferredReloadArtifact,
                        expectedPosition,
                        previous.Revision);
                }

                var isSeek = hasPrevious
                    && Math.Abs(positionTicks - expectedPosition) > Math.Max(0, seekThresholdTicks);

                if (isSeek
                    && hasPrevious
                    && previous.IsPlaying
                    && Math.Abs(positionTicks - previous.PositionTicks)
                        <= StationaryMasterReportTolerance.Ticks
                    && expectedPosition > positionTicks)
                {
                    return new MasterPositionUpdateResult(
                        MasterPositionUpdateKind.IgnoredStaleHeartbeat,
                        expectedPosition,
                        previous.Revision);
                }

                if (isSeek && !acceptInferredSeek)
                {
                    return new MasterPositionUpdateResult(
                        MasterPositionUpdateKind.IgnoredUnexpectedDiscontinuity,
                        expectedPosition,
                        hasPrevious ? previous.Revision : 0);
                }

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
                _reloadArtifacts.Remove(partyId);
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
                _reloadArtifacts.Remove(partyId);
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
                _lastPauseEchoStates.Remove(sessionId);
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
                _reloadArtifacts.Remove(partyId);
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
            bool clearOnMismatch,
            out IReadOnlyList<PauseStateExpectationToken> matchedExpectations)
        {
            matchedExpectations = Array.Empty<PauseStateExpectationToken>();
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
            _lastPauseEchoStates[sessionId] = new LastPauseEchoState
            {
                IsPaused = isPaused,
                EchoedAt = nowUtc
            };
            matchedExpectations = expectedStates[matchingIndex].TokenIds
                .OrderBy(tokenId => tokenId)
                .Select(tokenId => new PauseStateExpectationToken(tokenId))
                .ToArray();
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

        private sealed class LastPauseEchoState
        {
            public bool IsPaused { get; set; }
            public DateTime EchoedAt { get; set; }
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

        private sealed class ReloadArtifactState
        {
            public DateTime FirstObservedAt { get; set; }
            public long AuthoritativeRevision { get; set; }
            public bool HasStaleSample { get; set; }
            public long LastStalePositionTicks { get; set; }
            public DateTime LastStaleReportAt { get; set; }
        }
    }

    public enum MasterPositionUpdateKind
    {
        Continuous,
        Seek,
        DeferredReloadArtifact,
        IgnoredReloadArtifactReport,
        IgnoredUnexpectedDiscontinuity,
        IgnoredStaleHeartbeat
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
        public bool IsIgnoredReloadArtifactReport =>
            Kind == MasterPositionUpdateKind.IgnoredReloadArtifactReport;
        public bool IsIgnoredUnexpectedDiscontinuity =>
            Kind == MasterPositionUpdateKind.IgnoredUnexpectedDiscontinuity;
        public bool IsIgnoredStaleHeartbeat =>
            Kind == MasterPositionUpdateKind.IgnoredStaleHeartbeat;
        public long AuthoritativePositionTicks { get; }
        public long AuthoritativeRevision { get; }
    }
}
