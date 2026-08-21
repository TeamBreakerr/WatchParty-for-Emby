using System;
using System.Collections.Generic;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Learns the end-to-end Unpause acknowledgement latency independently for each
    /// Emby SessionId. Samples are bounded before entering an EWMA so one pathological
    /// callback cannot permanently over-correct later resume seeks.
    /// </summary>
    public sealed class ParticipantResumeLatencyEstimator
    {
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

        public void RecordResumeCommand(
            string sessionId,
            PauseStateExpectationToken expectation,
            DateTime sentAtUtc)
        {
            if (string.IsNullOrEmpty(sessionId) || expectation.IsEmpty)
            {
                return;
            }

            lock (_syncRoot)
            {
                var state = GetOrCreateState(sessionId);
                state.PendingExpectation = expectation;
                state.PendingCommandSentAtUtc = sentAtUtc;
            }
        }

        public bool TryRecordResumeAcknowledgement(
            string sessionId,
            PauseStateExpectationToken expectation,
            DateTime acknowledgedAtUtc,
            out TimeSpan observedLatency,
            out TimeSpan updatedEstimate)
        {
            return TryRecordResumeAcknowledgement(
                sessionId,
                new[] { expectation },
                acknowledgedAtUtc,
                out observedLatency,
                out updatedEstimate);
        }

        public bool TryRecordResumeAcknowledgement(
            string sessionId,
            IReadOnlyList<PauseStateExpectationToken> expectations,
            DateTime acknowledgedAtUtc,
            out TimeSpan observedLatency,
            out TimeSpan updatedEstimate)
        {
            observedLatency = default;
            updatedEstimate = _initialEstimate;
            if (string.IsNullOrEmpty(sessionId)
                || expectations == null
                || expectations.Count == 0)
            {
                return false;
            }

            lock (_syncRoot)
            {
                if (!_sessions.TryGetValue(sessionId, out var state)
                    || !state.PendingCommandSentAtUtc.HasValue
                    || !ContainsExpectation(
                        expectations,
                        state.PendingExpectation))
                {
                    updatedEstimate = state?.EstimatedLatency ?? _initialEstimate;
                    return false;
                }

                var elapsed = acknowledgedAtUtc - state.PendingCommandSentAtUtc.Value;
                if (elapsed < TimeSpan.Zero)
                {
                    updatedEstimate = state.EstimatedLatency;
                    return false;
                }

                ClearPendingObservation(state);
                if (elapsed > _sampleTimeout)
                {
                    updatedEstimate = state.EstimatedLatency;
                    return false;
                }

                observedLatency = Clamp(
                    elapsed,
                    _minimumEstimate,
                    _maximumEstimate);
                var estimatedTicks = state.EstimatedLatency.Ticks
                    + ((observedLatency.Ticks - state.EstimatedLatency.Ticks)
                        * _smoothingFactor);
                state.EstimatedLatency = TimeSpan.FromTicks(
                    (long)Math.Round(estimatedTicks, MidpointRounding.AwayFromZero));
                updatedEstimate = state.EstimatedLatency;
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

        public void CancelPendingResume(
            string sessionId,
            PauseStateExpectationToken expectation)
        {
            if (string.IsNullOrEmpty(sessionId) || expectation.IsEmpty)
            {
                return;
            }

            lock (_syncRoot)
            {
                if (_sessions.TryGetValue(sessionId, out var state)
                    && state.PendingExpectation.Value == expectation.Value)
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

        private static bool ContainsExpectation(
            IReadOnlyList<PauseStateExpectationToken> expectations,
            PauseStateExpectationToken expected)
        {
            if (expected.IsEmpty)
            {
                return false;
            }

            for (var index = 0; index < expectations.Count; index++)
            {
                if (expectations[index].Value == expected.Value)
                {
                    return true;
                }
            }

            return false;
        }

        private static void ClearPendingObservation(SessionLatencyState state)
        {
            state.PendingExpectation = default;
            state.PendingCommandSentAtUtc = null;
        }

        private sealed class SessionLatencyState
        {
            public TimeSpan EstimatedLatency { get; set; }
            public PauseStateExpectationToken PendingExpectation { get; set; }
            public DateTime? PendingCommandSentAtUtc { get; set; }
        }
    }
}
