using System;
using System.Collections.Generic;
using System.Threading;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Keeps a participant's membership alive across the Stop event Emby emits when
    /// iOS backgrounds a player. The registration is identity-bound, so an old stop
    /// cannot cancel or remove a replacement PlaySessionId. The caller decides when
    /// retention ends (normally when the master leaves); there is intentionally no
    /// wall-clock expiry while the master is still active.
    /// </summary>
    public sealed class ParticipantStopGraceTracker : IDisposable
    {
        private readonly object _syncRoot = new object();
        private readonly TimeSpan _gracePeriod;
        private readonly Dictionary<string, Registration> _registrations =
            new Dictionary<string, Registration>(StringComparer.Ordinal);

        public ParticipantStopGraceTracker(TimeSpan gracePeriod)
        {
            if (gracePeriod <= TimeSpan.Zero
                && gracePeriod != Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(nameof(gracePeriod));
            }

            _gracePeriod = gracePeriod;
        }

        public Registration Schedule(
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
                _gracePeriod);

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

        public bool TryMarkResumed(
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
                TimeSpan gracePeriod)
            {
                Key = key;
                PartyId = partyId;
                SessionId = sessionId;
                PlaySessionId = playSessionId;
                StoppedAtUtc = stoppedAtUtc;
                GracePeriod = gracePeriod;
            }

            internal string Key { get; }
            public string PartyId { get; }
            public string SessionId { get; }
            public string PlaySessionId { get; }
            public DateTime StoppedAtUtc { get; }
            public TimeSpan GracePeriod { get; }
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
