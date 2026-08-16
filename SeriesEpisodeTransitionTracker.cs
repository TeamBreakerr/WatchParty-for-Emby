using System;
using System.Collections.Concurrent;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Tracks remote PlayNow commands while a series-party session moves from one
    /// episode to another. The old episode can report Stop before the replacement
    /// episode reports Start, so callers must retain the session during this window.
    /// </summary>
    public sealed class SeriesEpisodeTransitionTracker
    {
        private readonly ConcurrentDictionary<string, DateTime> _expectedStarts =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);

        public void ExpectStart(string sessionId, string episodeItemId, DateTime expiresAtUtc)
        {
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(episodeItemId))
            {
                throw new ArgumentException("sessionId and episodeItemId are required");
            }

            _expectedStarts[GetKey(sessionId, episodeItemId)] = expiresAtUtc;
        }

        public void CancelExpectedStart(string sessionId, string episodeItemId)
        {
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(episodeItemId))
            {
                return;
            }

            _expectedStarts.TryRemove(GetKey(sessionId, episodeItemId), out _);
        }

        public bool IsExpectedStart(string sessionId, string episodeItemId, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(episodeItemId))
            {
                return false;
            }

            var key = GetKey(sessionId, episodeItemId);
            if (!_expectedStarts.TryGetValue(key, out var expiresAtUtc))
            {
                return false;
            }

            if (expiresAtUtc >= nowUtc)
            {
                return true;
            }

            _expectedStarts.TryRemove(key, out _);
            return false;
        }

        public bool ConsumeExpectedStart(string sessionId, string episodeItemId, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(episodeItemId))
            {
                return false;
            }

            return _expectedStarts.TryRemove(
                    GetKey(sessionId, episodeItemId),
                    out var expiresAtUtc)
                && expiresAtUtc >= nowUtc;
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
                if (expectedStart.Value < nowUtc)
                {
                    _expectedStarts.TryRemove(expectedStart.Key, out _);
                }
            }
        }

        private static string GetKey(string sessionId, string episodeItemId)
        {
            return sessionId + ":" + episodeItemId;
        }
    }
}
