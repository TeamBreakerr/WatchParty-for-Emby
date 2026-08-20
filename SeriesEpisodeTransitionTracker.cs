using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Tracks remote PlayNow commands while a series-party session moves from one
    /// episode to another. The old episode can report Stop before the replacement
    /// episode reports Start, so callers must retain the session during this window.
    /// </summary>
    public sealed class SeriesEpisodeTransitionTracker
    {
        private readonly ConcurrentDictionary<string, ExpectedEpisodeStart> _expectedStarts =
            new ConcurrentDictionary<string, ExpectedEpisodeStart>(StringComparer.Ordinal);

        public void ExpectStart(string sessionId, string episodeItemId, DateTime expiresAtUtc)
        {
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(episodeItemId))
            {
                throw new ArgumentException("sessionId and episodeItemId are required");
            }

            _expectedStarts[sessionId] = new ExpectedEpisodeStart(
                episodeItemId,
                expiresAtUtc,
                attemptCount: 0,
                lastAttemptAtUtc: DateTime.MinValue);
        }

        /// <summary>
        /// Starts the first PlayNow command for an episode, or a bounded retry when the
        /// client has not confirmed the target with PlaybackStart. Repeated callers from
        /// seek reconciliation and the periodic sync pass share this gate, so they cannot
        /// create a command storm for one session.
        /// </summary>
        public bool TryBeginCommandAttempt(
            string sessionId,
            string episodeItemId,
            DateTime nowUtc,
            TimeSpan retryInterval,
            int maxAttempts,
            out int attempt)
        {
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(episodeItemId))
            {
                throw new ArgumentException("sessionId and episodeItemId are required");
            }
            if (retryInterval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(retryInterval));
            }
            if (maxAttempts <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxAttempts));
            }

            while (true)
            {
                if (!_expectedStarts.TryGetValue(sessionId, out var current)
                    || !string.Equals(
                        current.EpisodeItemId,
                        episodeItemId,
                        StringComparison.Ordinal)
                    || (current.ExpiresAtUtc < nowUtc && current.AttemptCount == 0))
                {
                    var expirationTicks = retryInterval.Ticks * (maxAttempts + 1L);
                    var replacement = new ExpectedEpisodeStart(
                        episodeItemId,
                        nowUtc.AddTicks(expirationTicks),
                        attemptCount: 1,
                        lastAttemptAtUtc: nowUtc);
                    if (current == null)
                    {
                        if (!_expectedStarts.TryAdd(sessionId, replacement))
                        {
                            continue;
                        }
                    }
                    else if (!_expectedStarts.TryUpdate(sessionId, replacement, current))
                    {
                        continue;
                    }

                    attempt = 1;
                    return true;
                }

                if (current.AttemptCount >= maxAttempts
                    || nowUtc - current.LastAttemptAtUtc < retryInterval)
                {
                    attempt = current.AttemptCount;
                    return false;
                }

                var retry = new ExpectedEpisodeStart(
                    current.EpisodeItemId,
                    current.ExpiresAtUtc,
                    current.AttemptCount + 1,
                    nowUtc);
                if (!_expectedStarts.TryUpdate(sessionId, retry, current))
                {
                    continue;
                }

                attempt = retry.AttemptCount;
                return true;
            }
        }

        public void CancelExpectedStart(string sessionId, string episodeItemId)
        {
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(episodeItemId))
            {
                return;
            }

            if (_expectedStarts.TryGetValue(sessionId, out var expectedStart)
                && string.Equals(
                    expectedStart.EpisodeItemId,
                    episodeItemId,
                    StringComparison.Ordinal))
            {
                TryRemoveExpectedStart(sessionId, expectedStart);
            }
        }

        public bool IsExpectedStart(string sessionId, string episodeItemId, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(episodeItemId))
            {
                return false;
            }

            if (!_expectedStarts.TryGetValue(sessionId, out var expectedStart)
                || !string.Equals(
                    expectedStart.EpisodeItemId,
                    episodeItemId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            if (expectedStart.ExpiresAtUtc >= nowUtc)
            {
                return true;
            }

            // A bounded command attempt remains as a terminal tombstone after its
            // confirmation window. Otherwise the periodic sync pass would remove it
            // and silently start another three-attempt cycle forever. A new target
            // episode or ClearSession resets the gate.
            if (expectedStart.AttemptCount == 0)
            {
                TryRemoveExpectedStart(sessionId, expectedStart);
            }
            return false;
        }

        public bool ConsumeExpectedStart(string sessionId, string episodeItemId, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(episodeItemId))
            {
                return false;
            }

            if (!_expectedStarts.TryGetValue(sessionId, out var expectedStart)
                || !string.Equals(
                    expectedStart.EpisodeItemId,
                    episodeItemId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            return TryRemoveExpectedStart(sessionId, expectedStart)
                && expectedStart.ExpiresAtUtc >= nowUtc;
        }

        public bool ShouldRetainSessionOnStop(
            string sessionId,
            string stoppedEpisodeId,
            string currentEpisodeId,
            DateTime nowUtc)
        {
            return !string.IsNullOrEmpty(stoppedEpisodeId)
                && !string.IsNullOrEmpty(currentEpisodeId)
                && !string.Equals(
                    stoppedEpisodeId,
                    currentEpisodeId,
                    StringComparison.OrdinalIgnoreCase)
                && IsExpectedStart(sessionId, currentEpisodeId, nowUtc);
        }

        public void RemoveExpired(DateTime nowUtc)
        {
            foreach (var expectedStart in _expectedStarts)
            {
                if (expectedStart.Value.ExpiresAtUtc < nowUtc
                    && expectedStart.Value.AttemptCount == 0)
                {
                    TryRemoveExpectedStart(expectedStart.Key, expectedStart.Value);
                }
            }
        }

        public bool ClearSession(string sessionId)
        {
            return !string.IsNullOrEmpty(sessionId)
                && _expectedStarts.TryRemove(sessionId, out _);
        }

        private bool TryRemoveExpectedStart(
            string sessionId,
            ExpectedEpisodeStart expectedStart)
        {
            return ((ICollection<KeyValuePair<string, ExpectedEpisodeStart>>)_expectedStarts)
                .Remove(new KeyValuePair<string, ExpectedEpisodeStart>(sessionId, expectedStart));
        }

        private sealed class ExpectedEpisodeStart
        {
            public ExpectedEpisodeStart(
                string episodeItemId,
                DateTime expiresAtUtc,
                int attemptCount,
                DateTime lastAttemptAtUtc)
            {
                EpisodeItemId = episodeItemId;
                ExpiresAtUtc = expiresAtUtc;
                AttemptCount = attemptCount;
                LastAttemptAtUtc = lastAttemptAtUtc;
            }

            public string EpisodeItemId { get; }

            public DateTime ExpiresAtUtc { get; }

            public int AttemptCount { get; }

            public DateTime LastAttemptAtUtc { get; }
        }
    }
}
