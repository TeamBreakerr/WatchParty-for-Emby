using System;
using System.Collections.Generic;

namespace WatchPartyForEmby
{
    public enum MasterPlaybackStateObservationKind
    {
        None,
        Deferred,
        Cancelled,
        Committed
    }

    public readonly struct MasterPlaybackStateObservation
    {
        internal MasterPlaybackStateObservation(
            MasterPlaybackStateObservationKind kind,
            bool? pendingIsPaused)
        {
            Kind = kind;
            PendingIsPaused = pendingIsPaused;
        }

        public MasterPlaybackStateObservationKind Kind { get; }

        public bool? PendingIsPaused { get; }

        public bool ShouldDefer => Kind == MasterPlaybackStateObservationKind.Deferred;

        public bool ShouldCancel => Kind == MasterPlaybackStateObservationKind.Cancelled;

        public bool ShouldCommit => Kind == MasterPlaybackStateObservationKind.Committed;
    }

    /// <summary>
    /// Coalesces the short state oscillation emitted by Emby Web while it rebuilds a
    /// video element after a seek. A transition is not accepted until it remains the
    /// candidate for the debounce window; an opposite report cancels the candidate.
    /// The server owns the delayed commit, so this class remains deterministic and easy
    /// to test without timers or Emby dependencies.
    /// </summary>
    public sealed class MasterPlaybackStateDebouncer
    {
        private readonly object _syncRoot = new object();
        private readonly TimeSpan _window;
        private readonly Dictionary<string, PendingState> _pending =
            new Dictionary<string, PendingState>(StringComparer.Ordinal);

        public MasterPlaybackStateDebouncer(TimeSpan window)
        {
            if (window < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(window));
            }

            _window = window;
        }

        public TimeSpan Window => _window;

        public MasterPlaybackStateObservation Observe(
            string sessionId,
            bool stableIsPaused,
            bool reportedIsPaused,
            DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return new MasterPlaybackStateObservation(
                    MasterPlaybackStateObservationKind.None,
                    pendingIsPaused: null);
            }

            if (reportedIsPaused == stableIsPaused)
            {
                var cancelled = Cancel(sessionId);
                return new MasterPlaybackStateObservation(
                    cancelled
                        ? MasterPlaybackStateObservationKind.Cancelled
                        : MasterPlaybackStateObservationKind.None,
                    pendingIsPaused: null);
            }

            lock (_syncRoot)
            {
                if (!_pending.TryGetValue(sessionId, out var pending))
                {
                    _pending[sessionId] = new PendingState
                    {
                        IsPaused = reportedIsPaused,
                        FirstObservedAt = nowUtc
                    };
                    return new MasterPlaybackStateObservation(
                        MasterPlaybackStateObservationKind.Deferred,
                        reportedIsPaused);
                }

                if (pending.IsPaused != reportedIsPaused)
                {
                    _pending.Remove(sessionId);
                    return new MasterPlaybackStateObservation(
                        MasterPlaybackStateObservationKind.Cancelled,
                        pending.IsPaused);
                }

                if (nowUtc - pending.FirstObservedAt >= _window)
                {
                    _pending.Remove(sessionId);
                    return new MasterPlaybackStateObservation(
                        MasterPlaybackStateObservationKind.Committed,
                        reportedIsPaused);
                }

                return new MasterPlaybackStateObservation(
                    MasterPlaybackStateObservationKind.Deferred,
                    reportedIsPaused);
            }
        }

        public bool TryCommit(
            string sessionId,
            bool stableIsPaused,
            DateTime nowUtc,
            out bool committedIsPaused)
        {
            committedIsPaused = stableIsPaused;
            if (string.IsNullOrEmpty(sessionId))
            {
                return false;
            }

            lock (_syncRoot)
            {
                if (!_pending.TryGetValue(sessionId, out var pending)
                    || pending.IsPaused == stableIsPaused
                    || nowUtc - pending.FirstObservedAt < _window)
                {
                    return false;
                }

                _pending.Remove(sessionId);
                committedIsPaused = pending.IsPaused;
                return true;
            }
        }

        public bool Cancel(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return false;
            }

            lock (_syncRoot)
            {
                return _pending.Remove(sessionId);
            }
        }

        public void Clear()
        {
            lock (_syncRoot)
            {
                _pending.Clear();
            }
        }

        private sealed class PendingState
        {
            public bool IsPaused { get; set; }

            public DateTime FirstObservedAt { get; set; }
        }
    }
}
