using System;
using System.Collections.Generic;

namespace WatchPartyForEmby
{
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
        private readonly Dictionary<string, PendingSeekState> _pendingSeeks = new Dictionary<string, PendingSeekState>();
        private readonly Dictionary<string, List<ExpectedPauseState>> _expectedPauseStates =
            new Dictionary<string, List<ExpectedPauseState>>();
        private readonly Dictionary<string, MasterClockState> _masterClocks = new Dictionary<string, MasterClockState>();

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

        public void ExpectPauseState(string sessionId, bool isPaused, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return;
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
                if (expectedStates.Count > 0
                    && expectedStates[expectedStates.Count - 1].IsPaused == isPaused)
                {
                    // Refresh a duplicate command instead of creating an expectation that
                    // could consume a later intentional state transition twice.
                    expectedStates[expectedStates.Count - 1].ExpiresAt = expiresAt;
                }
                else
                {
                    expectedStates.Add(new ExpectedPauseState
                    {
                        IsPaused = isPaused,
                        ExpiresAt = expiresAt
                    });
                }
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

                // A client may acknowledge Pause after a newer Unpause was already sent.
                // Consume commands through the matching state in order, leaving newer
                // expectations queued for their own delayed echoes.
                expectedStates.RemoveRange(0, matchingIndex + 1);
                if (expectedStates.Count == 0)
                {
                    _expectedPauseStates.Remove(sessionId);
                }
                return true;
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
                    UpdatedAt = nowUtc
                };
                return isSeek;
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
                var preservedPosition = _masterClocks.TryGetValue(partyId, out var clock)
                    ? EstimatePosition(clock, nowUtc)
                    : fallbackPositionTicks;
                _masterClocks[partyId] = new MasterClockState
                {
                    PositionTicks = preservedPosition,
                    IsPlaying = isPlaying,
                    UpdatedAt = nowUtc
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
                    UpdatedAt = nowUtc
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
        }
    }
}
