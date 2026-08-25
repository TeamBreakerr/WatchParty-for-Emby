using System;
using System.Collections.Generic;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Confirms a replacement Emby Web PlaySessionId when the browser keeps reporting
    /// ordinary progress but omitted PlaybackStart. A replacement needs consecutive,
    /// plausible reports after the registered generation has gone quiet; one delayed or
    /// alternating zombie report can never replace the authoritative generation.
    /// </summary>
    public sealed class PlaybackGenerationCandidateTracker
    {
        private readonly object _syncRoot = new object();
        private readonly TimeSpan _currentGenerationQuietPeriod;
        private readonly TimeSpan _maximumConfirmationGap;
        private readonly TimeSpan _positionTolerance;
        private readonly int _requiredConfirmations;
        private readonly Dictionary<string, Candidate> _candidates =
            new Dictionary<string, Candidate>(StringComparer.Ordinal);

        public PlaybackGenerationCandidateTracker(
            TimeSpan currentGenerationQuietPeriod,
            TimeSpan maximumConfirmationGap,
            int requiredConfirmations,
            TimeSpan positionTolerance)
        {
            if (currentGenerationQuietPeriod <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(currentGenerationQuietPeriod));
            }

            if (maximumConfirmationGap <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumConfirmationGap));
            }

            if (requiredConfirmations < 2)
            {
                throw new ArgumentOutOfRangeException(nameof(requiredConfirmations));
            }

            if (positionTolerance < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(positionTolerance));
            }

            _currentGenerationQuietPeriod = currentGenerationQuietPeriod;
            _maximumConfirmationGap = maximumConfirmationGap;
            _requiredConfirmations = requiredConfirmations;
            _positionTolerance = positionTolerance;
        }

        public bool Observe(
            string partyId,
            string sessionId,
            string currentPlaySessionId,
            string candidatePlaySessionId,
            DateTime currentGenerationLastActivityAtUtc,
            DateTime observedAtUtc,
            long positionTicks,
            bool isPaused)
        {
            var key = Key(partyId, sessionId);
            if (string.IsNullOrEmpty(partyId)
                || string.IsNullOrEmpty(sessionId)
                || string.IsNullOrEmpty(currentPlaySessionId)
                || string.IsNullOrEmpty(candidatePlaySessionId)
                || string.Equals(
                    currentPlaySessionId,
                    candidatePlaySessionId,
                    StringComparison.Ordinal)
                || positionTicks < 0
                || observedAtUtc < currentGenerationLastActivityAtUtc
                || observedAtUtc - currentGenerationLastActivityAtUtc
                    < _currentGenerationQuietPeriod)
            {
                ResetByKey(key);
                return false;
            }

            lock (_syncRoot)
            {
                if (!_candidates.TryGetValue(key, out var previous)
                    || !string.Equals(
                        previous.CurrentPlaySessionId,
                        currentPlaySessionId,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        previous.CandidatePlaySessionId,
                        candidatePlaySessionId,
                        StringComparison.Ordinal)
                    || !IsPlausibleContinuation(
                        previous,
                        observedAtUtc,
                        positionTicks,
                        isPaused))
                {
                    _candidates[key] = new Candidate(
                        currentPlaySessionId,
                        candidatePlaySessionId,
                        observedAtUtc,
                        positionTicks,
                        isPaused,
                        confirmations: 1);
                    return false;
                }

                var confirmations = previous.Confirmations + 1;
                if (confirmations >= _requiredConfirmations)
                {
                    _candidates.Remove(key);
                    return true;
                }

                _candidates[key] = new Candidate(
                    currentPlaySessionId,
                    candidatePlaySessionId,
                    observedAtUtc,
                    positionTicks,
                    isPaused,
                    confirmations);
                return false;
            }
        }

        public void Reset(string partyId, string sessionId)
        {
            ResetByKey(Key(partyId, sessionId));
        }

        public void ClearParty(string partyId)
        {
            if (string.IsNullOrEmpty(partyId))
            {
                return;
            }

            var prefix = partyId + "\n";
            lock (_syncRoot)
            {
                var keys = new List<string>();
                foreach (var key in _candidates.Keys)
                {
                    if (key.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        keys.Add(key);
                    }
                }

                foreach (var key in keys)
                {
                    _candidates.Remove(key);
                }
            }
        }

        private bool IsPlausibleContinuation(
            Candidate previous,
            DateTime observedAtUtc,
            long positionTicks,
            bool isPaused)
        {
            var elapsed = observedAtUtc - previous.ObservedAtUtc;
            if (elapsed <= TimeSpan.Zero || elapsed > _maximumConfirmationGap)
            {
                return false;
            }

            var positionDelta = positionTicks - previous.PositionTicks;
            if (previous.IsPaused && isPaused)
            {
                return Math.Abs(positionDelta) <= _positionTolerance.Ticks;
            }

            return positionDelta >= -_positionTolerance.Ticks
                && positionDelta <= elapsed.Ticks + _positionTolerance.Ticks;
        }

        private void ResetByKey(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            lock (_syncRoot)
            {
                _candidates.Remove(key);
            }
        }

        private static string Key(string partyId, string sessionId)
        {
            return string.IsNullOrEmpty(partyId) || string.IsNullOrEmpty(sessionId)
                ? null
                : partyId + "\n" + sessionId;
        }

        private sealed class Candidate
        {
            public Candidate(
                string currentPlaySessionId,
                string candidatePlaySessionId,
                DateTime observedAtUtc,
                long positionTicks,
                bool isPaused,
                int confirmations)
            {
                CurrentPlaySessionId = currentPlaySessionId;
                CandidatePlaySessionId = candidatePlaySessionId;
                ObservedAtUtc = observedAtUtc;
                PositionTicks = positionTicks;
                IsPaused = isPaused;
                Confirmations = confirmations;
            }

            public string CurrentPlaySessionId { get; }
            public string CandidatePlaySessionId { get; }
            public DateTime ObservedAtUtc { get; }
            public long PositionTicks { get; }
            public bool IsPaused { get; }
            public int Confirmations { get; }
        }
    }
}
