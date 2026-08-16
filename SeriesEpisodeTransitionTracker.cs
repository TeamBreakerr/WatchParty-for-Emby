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
                expiresAtUtc);
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

            TryRemoveExpectedStart(sessionId, expectedStart);
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
                if (expectedStart.Value.ExpiresAtUtc < nowUtc)
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
            public ExpectedEpisodeStart(string episodeItemId, DateTime expiresAtUtc)
            {
                EpisodeItemId = episodeItemId;
                ExpiresAtUtc = expiresAtUtc;
            }

            public string EpisodeItemId { get; }

            public DateTime ExpiresAtUtc { get; }
        }
    }
}
