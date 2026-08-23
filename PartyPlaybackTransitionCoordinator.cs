using System;
using System.Collections.Concurrent;
using System.Threading;

namespace WatchPartyForEmby
{
    public sealed class PartyPlaybackTransitionRegistration
    {
        internal CancellationTokenSource Source { get; set; }
        public string PartyId { get; set; }
        public CancellationToken Token { get; set; }
    }

    /// <summary>
    /// Gives each party playback-state transition one cancellation generation. A newer
    /// Pause or Resume cancels the preceding generation so delayed retries cannot restore
    /// an obsolete authoritative state.
    /// </summary>
    public sealed class PartyPlaybackTransitionCoordinator : IDisposable
    {
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _current =
            new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        private readonly CancellationToken _lifetimeToken;

        public PartyPlaybackTransitionCoordinator(CancellationToken lifetimeToken)
        {
            _lifetimeToken = lifetimeToken;
        }

        public PartyPlaybackTransitionRegistration Begin(string partyId)
        {
            if (string.IsNullOrEmpty(partyId))
            {
                throw new ArgumentException("partyId is required", nameof(partyId));
            }

            var replacement = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
            while (true)
            {
                if (_current.TryGetValue(partyId, out var previous))
                {
                    if (!_current.TryUpdate(partyId, replacement, previous))
                    {
                        continue;
                    }

                    previous.Cancel();
                    break;
                }

                if (_current.TryAdd(partyId, replacement))
                {
                    break;
                }
            }

            return new PartyPlaybackTransitionRegistration
            {
                PartyId = partyId,
                Source = replacement,
                Token = replacement.Token
            };
        }

        public void Cancel(string partyId)
        {
            if (!string.IsNullOrEmpty(partyId)
                && _current.TryRemove(partyId, out var source))
            {
                source.Cancel();
            }
        }

        public void Complete(PartyPlaybackTransitionRegistration registration)
        {
            if (registration?.Source == null)
            {
                return;
            }

            if (_current.TryGetValue(registration.PartyId, out var current)
                && ReferenceEquals(current, registration.Source))
            {
                _current.TryRemove(registration.PartyId, out _);
            }
            registration.Source.Dispose();
            registration.Source = null;
        }

        public void Dispose()
        {
            foreach (var pair in _current)
            {
                if (_current.TryRemove(pair.Key, out var source))
                {
                    source.Cancel();
                    source.Dispose();
                }
            }
        }
    }
}
