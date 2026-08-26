using System;
using System.Collections.Concurrent;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Serializes master-role mutations and departure effects per party. Session event
    /// queues are keyed by Emby SessionId, so two candidate master sessions can otherwise
    /// race each other even though each individual session is processed in order.
    /// </summary>
    public sealed class MasterSessionLifecycleCoordinator
    {
        private readonly ConcurrentDictionary<string, object> _partyGates =
            new ConcurrentDictionary<string, object>(StringComparer.Ordinal);

        public void Execute(string partyId, Action operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            Execute(
                partyId,
                () =>
                {
                    operation();
                    return true;
                });
        }

        public TResult Execute<TResult>(string partyId, Func<TResult> operation)
        {
            if (string.IsNullOrEmpty(partyId))
            {
                throw new ArgumentException("partyId is required", nameof(partyId));
            }
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            var gate = _partyGates.GetOrAdd(partyId, _ => new object());
            lock (gate)
            {
                return operation();
            }
        }
    }
}
