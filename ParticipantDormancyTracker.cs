using System;
using System.Collections.Generic;
using System.Threading;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Keeps a participant's membership while separating it from command eligibility
    /// after Emby reports Stop. The registration is identity-bound, so another session
    /// cannot reactivate the stopped player. Stop remains available for teardown; all
    /// commands that can start, resume, pause, or seek playback stay blocked until the
    /// client reports its own accepted Start/Progress.
    /// </summary>
    public enum ParticipantRoomCommand
    {
        PlayNow,
        Pause,
        Resume,
        Seek,
        Stop
    }

    public sealed class ParticipantDormancyTracker : IDisposable
    {
        private readonly object _syncRoot = new object();
        private readonly TimeSpan _retentionPeriod;
        private readonly Dictionary<string, Registration> _registrations =
            new Dictionary<string, Registration>(StringComparer.Ordinal);

        public ParticipantDormancyTracker(TimeSpan retentionPeriod)
        {
            if (retentionPeriod <= TimeSpan.Zero
                && retentionPeriod != Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(nameof(retentionPeriod));
            }

            _retentionPeriod = retentionPeriod;
        }

        public Registration MarkDormant(
            string partyId,
            string sessionId,
            string playSessionId,
            DateTime stoppedAtUtc)
        {
            if (string.IsNullOrEmpty(partyId) || string.IsNullOrEmpty(sessionId))
            {
                throw new ArgumentException("partyId and sessionId are required");
            }

            var registration = new Registration(
                Key(partyId, sessionId),
                partyId,
                sessionId,
                playSessionId,
                stoppedAtUtc,
                _retentionPeriod);

            lock (_syncRoot)
            {
                if (_registrations.TryGetValue(registration.Key, out var previous))
                {
                    previous.Cancel();
                }

                _registrations[registration.Key] = registration;
            }

            return registration;
        }

        public bool TryReactivate(
            string partyId,
            string sessionId,
            DateTime activityAtUtc,
            out Registration registration)
        {
            registration = null;
            var key = Key(partyId, sessionId);
            lock (_syncRoot)
            {
                if (!_registrations.TryGetValue(key, out var current)
                    || activityAtUtc < current.StoppedAtUtc)
                {
                    return false;
                }

                _registrations.Remove(key);
                current.Cancel();
                registration = current;
                return true;
            }
        }

        /// <summary>
        /// Returns whether a room command may target this retained participant. A Stop
        /// registration is also a dormancy marker: membership stays intact, but the
        /// player cannot be restarted or controlled until its own Start/Progress report
        /// claims the registration through <see cref="TryReactivate"/>.
        /// </summary>
        public bool CanReceiveCommand(
            string partyId,
            string sessionId,
            ParticipantRoomCommand command)
        {
            if (command == ParticipantRoomCommand.Stop)
            {
                return true;
            }

            var key = Key(partyId, sessionId);
            lock (_syncRoot)
            {
                return !_registrations.ContainsKey(key);
            }
        }

        public bool TryClaim(Registration registration)
        {
            if (registration == null)
            {
                return false;
            }

            lock (_syncRoot)
            {
                if (!_registrations.TryGetValue(registration.Key, out var current)
                    || !ReferenceEquals(current, registration))
                {
                    return false;
                }

                _registrations.Remove(registration.Key);
                return true;
            }
        }

        public void Cancel(string partyId, string sessionId)
        {
            var key = Key(partyId, sessionId);
            lock (_syncRoot)
            {
                if (_registrations.TryGetValue(key, out var registration))
                {
                    _registrations.Remove(key);
                    registration.Cancel();
                }
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
                var prefix = partyId + "\n";
                var keys = new List<string>();
                foreach (var pair in _registrations)
                {
                    if (pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        pair.Value.Cancel();
                        pair.Value.Dispose();
                        keys.Add(pair.Key);
                    }
                }

                foreach (var key in keys)
                {
                    _registrations.Remove(key);
                }
            }
        }

        public void Dispose()
        {
            lock (_syncRoot)
            {
                foreach (var registration in _registrations.Values)
                {
                    registration.Cancel();
                    registration.Dispose();
                }

                _registrations.Clear();
            }
        }

        private static string Key(string partyId, string sessionId)
        {
            return (partyId ?? string.Empty) + "\n" + (sessionId ?? string.Empty);
        }

        public sealed class Registration : IDisposable
        {
            private readonly CancellationTokenSource _cancellation =
                new CancellationTokenSource();

            internal Registration(
                string key,
                string partyId,
                string sessionId,
                string playSessionId,
                DateTime stoppedAtUtc,
                TimeSpan retentionPeriod)
            {
                Key = key;
                PartyId = partyId;
                SessionId = sessionId;
                PlaySessionId = playSessionId;
                StoppedAtUtc = stoppedAtUtc;
                RetentionPeriod = retentionPeriod;
            }

            internal string Key { get; }
            public string PartyId { get; }
            public string SessionId { get; }
            public string PlaySessionId { get; }
            public DateTime StoppedAtUtc { get; }
            public TimeSpan RetentionPeriod { get; }
            public CancellationToken CancellationToken => _cancellation.Token;

            internal void Cancel()
            {
                if (!_cancellation.IsCancellationRequested)
                {
                    _cancellation.Cancel();
                }
            }

            public void Dispose()
            {
                _cancellation.Dispose();
            }
        }
    }
}
