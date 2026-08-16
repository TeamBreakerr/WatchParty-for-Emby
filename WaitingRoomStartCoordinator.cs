using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Bridges authenticated API requests and automatic readiness checks to the one
    /// running server entry point. Starts for the same party share one in-flight task.
    /// </summary>
    public sealed class WaitingRoomStartCoordinator
    {
        private readonly object _registrationSync = new object();
        private readonly ConcurrentDictionary<string, Lazy<Task<bool>>> _activeStarts =
            new ConcurrentDictionary<string, Lazy<Task<bool>>>(StringComparer.Ordinal);
        private Func<string, Task<bool>> _runtimeStart;

        public IDisposable Register(Func<string, Task<bool>> runtimeStart)
        {
            if (runtimeStart == null)
            {
                throw new ArgumentNullException(nameof(runtimeStart));
            }

            lock (_registrationSync)
            {
                if (_runtimeStart != null)
                {
                    throw new InvalidOperationException(
                        "A waiting-room runtime start handler is already registered.");
                }

                _runtimeStart = runtimeStart;
            }

            return new Registration(this, runtimeStart);
        }

        public Task<bool> StartAsync(string partyId)
        {
            if (string.IsNullOrWhiteSpace(partyId))
            {
                throw new ArgumentException("A party ID is required.", nameof(partyId));
            }

            Func<string, Task<bool>> runtimeStart;
            lock (_registrationSync)
            {
                runtimeStart = _runtimeStart;
            }

            if (runtimeStart == null)
            {
                throw new InvalidOperationException(
                    "The Watch Party playback runtime is not available.");
            }

            var requestedStart = new Lazy<Task<bool>>(
                () => runtimeStart(partyId),
                LazyThreadSafetyMode.ExecutionAndPublication);
            var activeStart = _activeStarts.GetOrAdd(partyId, requestedStart);
            return AwaitAndReleaseAsync(partyId, activeStart);
        }

        private async Task<bool> AwaitAndReleaseAsync(
            string partyId,
            Lazy<Task<bool>> activeStart)
        {
            try
            {
                return await activeStart.Value.ConfigureAwait(false);
            }
            finally
            {
                if (_activeStarts.TryGetValue(partyId, out var current)
                    && ReferenceEquals(current, activeStart))
                {
                    _activeStarts.TryRemove(partyId, out _);
                }
            }
        }

        private void Unregister(Func<string, Task<bool>> runtimeStart)
        {
            lock (_registrationSync)
            {
                if (ReferenceEquals(_runtimeStart, runtimeStart))
                {
                    _runtimeStart = null;
                }
            }
        }

        private sealed class Registration : IDisposable
        {
            private WaitingRoomStartCoordinator _owner;
            private readonly Func<string, Task<bool>> _runtimeStart;

            public Registration(
                WaitingRoomStartCoordinator owner,
                Func<string, Task<bool>> runtimeStart)
            {
                _owner = owner;
                _runtimeStart = runtimeStart;
            }

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                owner?.Unregister(_runtimeStart);
            }
        }
    }
}
