using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WatchPartyForEmby
{
    public sealed class SeriesSelectionCoordinator
    {
        private readonly object _syncRoot = new object();
        private readonly Dictionary<string, SeriesSelectionRegistration> _latestByParty =
            new Dictionary<string, SeriesSelectionRegistration>(StringComparer.Ordinal);

        public SeriesSelectionRegistration Register(string partyId, string episodeItemId)
        {
            if (string.IsNullOrEmpty(partyId) || string.IsNullOrEmpty(episodeItemId))
            {
                throw new ArgumentException("partyId and episodeItemId are required");
            }

            var registration = new SeriesSelectionRegistration(
                partyId,
                episodeItemId);
            SeriesSelectionRegistration superseded = null;
            lock (_syncRoot)
            {
                _latestByParty.TryGetValue(partyId, out superseded);
                _latestByParty[partyId] = registration;
            }
            superseded?.Supersede();

            return registration;
        }

        public async Task<bool> RunIfLatestAsync(
            SeriesSelectionRegistration registration,
            TimeSpan quietPeriod,
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken)
        {
            if (registration == null)
            {
                throw new ArgumentNullException(nameof(registration));
            }

            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (quietPeriod < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(quietPeriod));
            }

            try
            {
                if (!IsLatest(registration))
                {
                    return false;
                }

                using (var quietCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    registration.SupersededToken))
                {
                    if (quietPeriod > TimeSpan.Zero)
                    {
                        await Task.Delay(
                                quietPeriod,
                                quietCancellation.Token)
                            .ConfigureAwait(false);
                    }
                }

                if (!IsLatest(registration))
                {
                    return false;
                }

                // Once the batch starts, it must run to completion. A replacement
                // may supersede the selection for the next batch, but cancelling this
                // operation would leave different participant sessions on different
                // episodes.
                await operation(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (
                registration.IsSuperseded
                && !cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            finally
            {
                Complete(registration);
            }
        }

        public bool IsLatest(SeriesSelectionRegistration registration)
        {
            if (registration == null)
            {
                return false;
            }

            lock (_syncRoot)
            {
                return _latestByParty.TryGetValue(registration.PartyId, out var latest)
                    && ReferenceEquals(latest, registration);
            }
        }

        public void ClearParty(string partyId)
        {
            if (string.IsNullOrEmpty(partyId))
            {
                return;
            }

            SeriesSelectionRegistration removed = null;
            lock (_syncRoot)
            {
                if (_latestByParty.TryGetValue(partyId, out removed))
                {
                    _latestByParty.Remove(partyId);
                }
            }
            removed?.Supersede();
        }

        public void Cancel(SeriesSelectionRegistration registration)
        {
            if (registration == null)
            {
                return;
            }

            var removed = false;
            lock (_syncRoot)
            {
                if (_latestByParty.TryGetValue(registration.PartyId, out var latest)
                    && ReferenceEquals(latest, registration))
                {
                    _latestByParty.Remove(registration.PartyId);
                    removed = true;
                }
            }

            if (removed)
            {
                registration.Supersede();
            }
        }

        private void Complete(SeriesSelectionRegistration registration)
        {
            lock (_syncRoot)
            {
                if (_latestByParty.TryGetValue(registration.PartyId, out var latest)
                    && ReferenceEquals(latest, registration))
                {
                    _latestByParty.Remove(registration.PartyId);
                }
            }
        }
    }

    public sealed class SeriesSelectionRegistration
    {
        private readonly CancellationTokenSource _superseded =
            new CancellationTokenSource();
        private int _isSuperseded;

        internal SeriesSelectionRegistration(
            string partyId,
            string episodeItemId)
        {
            PartyId = partyId;
            EpisodeItemId = episodeItemId;
        }

        public string PartyId { get; }

        public string EpisodeItemId { get; }

        internal CancellationToken SupersededToken => _superseded.Token;

        internal bool IsSuperseded => Volatile.Read(ref _isSuperseded) != 0;

        internal void Supersede()
        {
            if (Interlocked.Exchange(ref _isSuperseded, 1) == 0)
            {
                _superseded.Cancel();
            }
        }
    }
}
