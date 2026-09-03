using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Spaces out the participant commands that make a client open a new read against
    /// the media provider.
    ///
    /// A 115 file serves two requests that start together and answers a third with
    /// 403. The limit is on requests that start at the same moment, not on connections
    /// that are already open: reads spaced half a second apart all succeed, and four
    /// slow readers can hold the same file open at once. Participants only ever start
    /// together because the server told them to, so the party fan-out is the one place
    /// that can keep a burst inside what the provider serves. A client playing a
    /// direct link has no way to retry the 403 it would otherwise receive.
    ///
    /// Commands are released <c>burstSize</c> at a time, one window apart, per party.
    /// A party of two therefore never waits, which is the common case; a third
    /// participant is delayed by a single window instead of failing.
    /// </summary>
    public sealed class ProviderBurstPacer
    {
        private const int PruneThreshold = 16;

        private readonly int _burstSize;
        private readonly TimeSpan _window;
        private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
        private readonly Func<DateTime> _utcNow;
        private readonly object _syncRoot = new object();
        private readonly Dictionary<string, BurstWindow> _windows =
            new Dictionary<string, BurstWindow>(StringComparer.Ordinal);

        public ProviderBurstPacer(
            int burstSize,
            TimeSpan window,
            Func<TimeSpan, CancellationToken, Task> delayAsync = null,
            Func<DateTime> utcNow = null)
        {
            if (burstSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(burstSize));
            }
            if (window <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(window));
            }

            _burstSize = burstSize;
            _window = window;
            _delayAsync = delayAsync ?? Task.Delay;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>
        /// Gets the number of parties with a live burst window. Operational diagnostics
        /// only; it returns to zero once idle windows are pruned.
        /// </summary>
        public int TrackedKeyCount
        {
            get
            {
                lock (_syncRoot)
                {
                    return _windows.Count;
                }
            }
        }

        /// <summary>
        /// True for the commands that make a participant open a new provider read.
        /// Pause and Stop end a read rather than starting one, so delaying them would
        /// only cost synchronization accuracy.
        /// </summary>
        public static bool OpensProviderRead(ParticipantRoomCommand command)
        {
            return command == ParticipantRoomCommand.PlayNow
                || command == ParticipantRoomCommand.Resume
                || command == ParticipantRoomCommand.Seek;
        }

        public Task PaceAsync(
            string key,
            ParticipantRoomCommand command,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(key) || !OpensProviderRead(command))
            {
                return Task.CompletedTask;
            }

            var delay = Reserve(key);
            return delay <= TimeSpan.Zero
                ? Task.CompletedTask
                : _delayAsync(delay, cancellationToken);
        }

        /// <summary>
        /// Claims the next dispatch slot for a party and returns how long the caller
        /// must wait before using it.
        /// </summary>
        public TimeSpan Reserve(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("key is required", nameof(key));
            }

            lock (_syncRoot)
            {
                var now = _utcNow();
                if (!_windows.TryGetValue(key, out var window)
                    || now >= window.StartUtc + _window)
                {
                    // Nothing started recently enough to burst against, so this command
                    // opens a fresh window and goes out immediately.
                    window = new BurstWindow(now);
                    _windows[key] = window;
                }
                else if (window.Issued >= _burstSize)
                {
                    window.StartUtc += _window;
                    window.Issued = 0;
                }

                window.Issued++;
                Prune(now);
                return window.StartUtc <= now
                    ? TimeSpan.Zero
                    : window.StartUtc - now;
            }
        }

        private void Prune(DateTime now)
        {
            if (_windows.Count <= PruneThreshold)
            {
                return;
            }

            // A window that can no longer delay anything carries no state worth keeping.
            var retention = TimeSpan.FromTicks(_window.Ticks * 4);
            List<string> expired = null;
            foreach (var entry in _windows)
            {
                if (now >= entry.Value.StartUtc + retention)
                {
                    (expired ?? (expired = new List<string>())).Add(entry.Key);
                }
            }

            if (expired == null)
            {
                return;
            }

            foreach (var key in expired)
            {
                _windows.Remove(key);
            }
        }

        private sealed class BurstWindow
        {
            public BurstWindow(DateTime startUtc)
            {
                StartUtc = startUtc;
            }

            public DateTime StartUtc { get; set; }

            public int Issued { get; set; }
        }
    }
}
