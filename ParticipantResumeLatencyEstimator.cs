using System;
using System.Collections.Generic;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Learns end-to-end resume latency independently for each Emby SessionId. A sample
    /// is accepted only after an unpaused client reports real forward media progress;
    /// a synthetic Unpause or Seek echo by itself is never treated as playback.
    /// </summary>
    public sealed class ParticipantResumeLatencyEstimator
    {
        private static readonly long MinimumForwardProgressTicks =
            TimeSpan.FromMilliseconds(100).Ticks;
        private static readonly long TargetEligibilityToleranceTicks =
            TimeSpan.FromSeconds(1.5).Ticks;

        private readonly object _syncRoot = new object();
        private readonly Dictionary<string, SessionLatencyState> _sessions =
            new Dictionary<string, SessionLatencyState>(StringComparer.Ordinal);
        private readonly TimeSpan _initialEstimate;
        private readonly TimeSpan _minimumEstimate;
        private readonly TimeSpan _maximumEstimate;
        private readonly TimeSpan _sampleTimeout;
        private readonly double _smoothingFactor;

        public ParticipantResumeLatencyEstimator(
            TimeSpan initialEstimate,
            TimeSpan minimumEstimate,
            TimeSpan maximumEstimate,
            TimeSpan sampleTimeout,
            double smoothingFactor)
        {
            if (minimumEstimate < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(minimumEstimate));
            }
            if (maximumEstimate < minimumEstimate)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumEstimate));
            }
            if (sampleTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(sampleTimeout));
            }
            if (smoothingFactor <= 0 || smoothingFactor > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(smoothingFactor));
            }

            _minimumEstimate = minimumEstimate;
            _maximumEstimate = maximumEstimate;
            _initialEstimate = Clamp(initialEstimate, minimumEstimate, maximumEstimate);
            _sampleTimeout = sampleTimeout;
            _smoothingFactor = smoothingFactor;
        }

        public TimeSpan GetEstimatedLatency(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return _initialEstimate;
            }

            lock (_syncRoot)
            {
                return _sessions.TryGetValue(sessionId, out var state)
                    ? state.EstimatedLatency
                    : _initialEstimate;
            }
        }

        public void RecordResumeSeek(
            string sessionId,
            long targetPositionTicks,
            DateTime sentAtUtc)
        {
            if (string.IsNullOrEmpty(sessionId) || targetPositionTicks < 0)
            {
                return;
            }

            lock (_syncRoot)
            {
                var state = GetOrCreateState(sessionId);
                state.PendingTargetPositionTicks = targetPositionTicks;
                state.PendingSeekSentAtUtc = sentAtUtc;
                state.BaselinePositionTicks = null;
            }
        }

        public bool TryRecordPlaybackProgress(
            string sessionId,
            long positionTicks,
            bool isPaused,
            DateTime reportedAtUtc,
            out TimeSpan observedLatency,
            out TimeSpan updatedEstimate)
        {
            observedLatency = default;
            updatedEstimate = _initialEstimate;
            if (string.IsNullOrEmpty(sessionId) || positionTicks < 0 || isPaused)
            {
                return false;
            }

            lock (_syncRoot)
            {
                if (!_sessions.TryGetValue(sessionId, out var state)
                    || !state.PendingSeekSentAtUtc.HasValue
                    || !state.PendingTargetPositionTicks.HasValue)
                {
                    updatedEstimate = state?.EstimatedLatency ?? _initialEstimate;
                    return false;
                }

                var elapsed = reportedAtUtc - state.PendingSeekSentAtUtc.Value;
                if (elapsed < TimeSpan.Zero)
                {
                    updatedEstimate = state.EstimatedLatency;
                    return false;
                }
                if (elapsed > _sampleTimeout)
                {
                    ClearPendingObservation(state);
                    updatedEstimate = state.EstimatedLatency;
                    return false;
                }

                var targetPositionTicks = state.PendingTargetPositionTicks.Value;
                if (positionTicks
                    < targetPositionTicks - TargetEligibilityToleranceTicks)
                {
                    updatedEstimate = state.EstimatedLatency;
                    return false;
                }
                var maximumPlausiblePositionTicks = targetPositionTicks
                    + elapsed.Ticks
                    + TargetEligibilityToleranceTicks;
                if (positionTicks > maximumPlausiblePositionTicks)
                {
                    updatedEstimate = state.EstimatedLatency;
                    return false;
                }

                if (!state.BaselinePositionTicks.HasValue)
                {
                    state.BaselinePositionTicks = positionTicks;
                    updatedEstimate = state.EstimatedLatency;
                    return false;
                }

                if (positionTicks - state.BaselinePositionTicks.Value
                    < MinimumForwardProgressTicks)
                {
                    updatedEstimate = state.EstimatedLatency;
                    return false;
                }

                var mediaAdvanceTicks = Math.Max(
                    0,
                    positionTicks - targetPositionTicks);
                observedLatency = Clamp(
                    elapsed - TimeSpan.FromTicks(mediaAdvanceTicks),
                    _minimumEstimate,
                    _maximumEstimate);
                var estimatedTicks = state.EstimatedLatency.Ticks
                    + ((observedLatency.Ticks - state.EstimatedLatency.Ticks)
                        * _smoothingFactor);
                state.EstimatedLatency = TimeSpan.FromTicks(
                    (long)Math.Round(
                        estimatedTicks,
                        MidpointRounding.AwayFromZero));
                updatedEstimate = state.EstimatedLatency;
                ClearPendingObservation(state);
                return true;
            }
        }

        public void CancelPendingResume(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return;
            }

            lock (_syncRoot)
            {
                if (_sessions.TryGetValue(sessionId, out var state))
                {
                    ClearPendingObservation(state);
                }
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
                _sessions.Remove(sessionId);
            }
        }

        public void Clear()
        {
            lock (_syncRoot)
            {
                _sessions.Clear();
            }
        }

        private SessionLatencyState GetOrCreateState(string sessionId)
        {
            if (!_sessions.TryGetValue(sessionId, out var state))
            {
                state = new SessionLatencyState
                {
                    EstimatedLatency = _initialEstimate
                };
                _sessions[sessionId] = state;
            }

            return state;
        }

        private static TimeSpan Clamp(
            TimeSpan value,
            TimeSpan minimum,
            TimeSpan maximum)
        {
            if (value < minimum)
            {
                return minimum;
            }
            if (value > maximum)
            {
                return maximum;
            }
            return value;
        }

        private static void ClearPendingObservation(SessionLatencyState state)
        {
            state.PendingTargetPositionTicks = null;
            state.PendingSeekSentAtUtc = null;
            state.BaselinePositionTicks = null;
        }

        private sealed class SessionLatencyState
        {
            public TimeSpan EstimatedLatency { get; set; }
            public long? PendingTargetPositionTicks { get; set; }
            public DateTime? PendingSeekSentAtUtc { get; set; }
            public long? BaselinePositionTicks { get; set; }
        }
    }
}
