using System;
using System.Collections.Generic;
using System.Linq;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Learns how long a participant takes to actually resume after it is told to,
    /// so the resume target can be offset by that much and both ends start together.
    ///
    /// The estimate is remembered per room, not per session alone. Resume latency is
    /// dominated by what the client has to do to produce the first frame, and that is
    /// a property of the media: a title the client direct-plays resumes in about half
    /// a second, while one that restarts a transcode takes two or three. Measured on
    /// one iPhone in one evening, the samples were 530, 653, 991, 1351, 1446, 1709,
    /// 1990 and 2630 ms - two clusters, not one population, and no single scalar fits
    /// both. A room is bound to its content, so keying the estimate by room separates
    /// those clusters without having to classify play methods.
    ///
    /// The memory also survives a participant leaving. It used to be discarded with
    /// the participant, so every room switch dropped back to a zero estimate and the
    /// first resume in the new room was not compensated at all - which is most of the
    /// error a viewer actually notices, because switching rooms is exactly when people
    /// press play.
    /// </summary>
    public sealed class ParticipantResumeLatencyEstimator
    {
        private static readonly long MinimumForwardProgressTicks =
            TimeSpan.FromMilliseconds(100).Ticks;
        private static readonly long TargetEligibilityToleranceTicks =
            TimeSpan.FromSeconds(1.5).Ticks;

        /// <summary>
        /// Rooms remembered per estimator. Well above any realistic room count; the
        /// bound only exists so a long-lived server cannot accumulate entries for
        /// sessions that never come back.
        /// </summary>
        private const int MaximumRememberedRooms = 256;

        private readonly object _syncRoot = new object();
        private readonly Dictionary<string, PendingResume> _pending =
            new Dictionary<string, PendingResume>(StringComparer.Ordinal);
        private readonly Dictionary<string, LearnedLatency> _learned =
            new Dictionary<string, LearnedLatency>(StringComparer.Ordinal);
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

        /// <summary>
        /// The compensation to apply for this session in this room. A room with no
        /// measurement of its own borrows what the same session learned elsewhere,
        /// which is a far better opening guess than assuming an instant resume.
        /// </summary>
        public TimeSpan GetEstimatedLatency(string partyId, string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return _initialEstimate;
            }

            lock (_syncRoot)
            {
                return CurrentEstimateLocked(partyId, sessionId);
            }
        }

        public void RecordResumeSeek(
            string partyId,
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
                _pending[sessionId] = new PendingResume
                {
                    PartyId = partyId,
                    TargetPositionTicks = targetPositionTicks,
                    SeekSentAtUtc = sentAtUtc,
                    BaselinePositionTicks = null
                };
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
                if (!_pending.TryGetValue(sessionId, out var pending))
                {
                    return false;
                }

                updatedEstimate = CurrentEstimateLocked(pending.PartyId, sessionId);

                var elapsed = reportedAtUtc - pending.SeekSentAtUtc;
                if (elapsed < TimeSpan.Zero)
                {
                    return false;
                }
                if (elapsed > _sampleTimeout)
                {
                    _pending.Remove(sessionId);
                    return false;
                }

                var targetPositionTicks = pending.TargetPositionTicks;
                if (positionTicks
                    < targetPositionTicks - TargetEligibilityToleranceTicks)
                {
                    return false;
                }
                var maximumPlausiblePositionTicks = targetPositionTicks
                    + elapsed.Ticks
                    + TargetEligibilityToleranceTicks;
                if (positionTicks > maximumPlausiblePositionTicks)
                {
                    return false;
                }

                if (!pending.BaselinePositionTicks.HasValue)
                {
                    pending.BaselinePositionTicks = positionTicks;
                    return false;
                }

                if (positionTicks - pending.BaselinePositionTicks.Value
                    < MinimumForwardProgressTicks)
                {
                    return false;
                }

                var mediaAdvanceTicks = Math.Max(
                    0,
                    positionTicks - targetPositionTicks);
                observedLatency = Clamp(
                    elapsed - TimeSpan.FromTicks(mediaAdvanceTicks),
                    _minimumEstimate,
                    _maximumEstimate);

                updatedEstimate = ApplyObservationLocked(
                    pending.PartyId,
                    sessionId,
                    observedLatency,
                    reportedAtUtc);
                _pending.Remove(sessionId);
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
                _pending.Remove(sessionId);
            }
        }

        /// <summary>
        /// Drops the in-flight observation for a session that is leaving. What the
        /// session has already learned is deliberately kept: the same device rejoining
        /// the same room resumes exactly as slowly as it did before, and re-measuring
        /// that from scratch costs the viewer one uncompensated resume every time.
        /// </summary>
        public void ClearSession(string sessionId)
        {
            CancelPendingResume(sessionId);
        }

        public void Clear()
        {
            lock (_syncRoot)
            {
                _pending.Clear();
                _learned.Clear();
            }
        }

        private TimeSpan CurrentEstimateLocked(string partyId, string sessionId)
        {
            if (_learned.TryGetValue(RoomKey(partyId, sessionId), out var learned)
                && learned.HasSample)
            {
                return learned.Value;
            }

            return SeedForSessionLocked(sessionId);
        }

        /// <summary>
        /// A room with no history of its own starts from the mean of what this session
        /// measured in the rooms it has played, rather than from zero.
        /// </summary>
        private TimeSpan SeedForSessionLocked(string sessionId)
        {
            var samples = _learned.Values
                .Where(entry => entry.HasSample
                    && string.Equals(entry.SessionId, sessionId, StringComparison.Ordinal))
                .Select(entry => entry.Value.Ticks)
                .ToList();

            if (samples.Count == 0)
            {
                return _initialEstimate;
            }

            return Clamp(
                TimeSpan.FromTicks((long)Math.Round(samples.Average())),
                _minimumEstimate,
                _maximumEstimate);
        }

        private TimeSpan ApplyObservationLocked(
            string partyId,
            string sessionId,
            TimeSpan observedLatency,
            DateTime observedAtUtc)
        {
            var key = RoomKey(partyId, sessionId);
            if (!_learned.TryGetValue(key, out var learned))
            {
                learned = new LearnedLatency { SessionId = sessionId };
                _learned[key] = learned;
                EvictSurplusRoomsLocked();
            }

            if (!learned.HasSample)
            {
                // With no measurement for this room yet, the measurement is the best
                // estimate there is. Blending it into a borrowed or zero prior only
                // guarantees the next resume is compensated by a fraction of what it
                // needs, which is how the first two resumes in every room used to be
                // systematically short.
                learned.Value = observedLatency;
                learned.HasSample = true;
            }
            else
            {
                var estimatedTicks = learned.Value.Ticks
                    + ((observedLatency.Ticks - learned.Value.Ticks)
                        * _smoothingFactor);
                learned.Value = TimeSpan.FromTicks(
                    (long)Math.Round(estimatedTicks, MidpointRounding.AwayFromZero));
            }

            learned.UpdatedAtUtc = observedAtUtc;
            return learned.Value;
        }

        private void EvictSurplusRoomsLocked()
        {
            while (_learned.Count > MaximumRememberedRooms)
            {
                var oldest = _learned
                    .OrderBy(entry => entry.Value.UpdatedAtUtc)
                    .First();
                _learned.Remove(oldest.Key);
            }
        }

        private static string RoomKey(string partyId, string sessionId)
        {
            return (partyId ?? string.Empty) + "\n" + sessionId;
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

        private sealed class PendingResume
        {
            public string PartyId { get; set; }
            public long TargetPositionTicks { get; set; }
            public DateTime SeekSentAtUtc { get; set; }
            public long? BaselinePositionTicks { get; set; }
        }

        private sealed class LearnedLatency
        {
            public string SessionId { get; set; }
            public TimeSpan Value { get; set; }
            public bool HasSample { get; set; }
            public DateTime UpdatedAtUtc { get; set; }
        }
    }
}
