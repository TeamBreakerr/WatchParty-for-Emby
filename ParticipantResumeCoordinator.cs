using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Keeps each participant's Resume-to-Seek pipeline ordered while allowing
    /// unrelated sessions to complete independently.
    /// </summary>
    public sealed class ParticipantResumeCoordinator
    {
        private readonly object _syncRoot = new object();
        private readonly KeyedAsyncSerialQueue<string> _sessionQueue;
        private readonly Dictionary<string, HashSet<PipelineRegistration>> _registrations =
            new Dictionary<string, HashSet<PipelineRegistration>>(StringComparer.Ordinal);

        public ParticipantResumeCoordinator(int capacityPerSession)
        {
            _sessionQueue = new KeyedAsyncSerialQueue<string>(capacityPerSession);
        }

        public async Task<bool> ResumeAsync(
            string sessionId,
            Func<CancellationToken, Task<bool>> sendResume,
            Func<long> getTargetPositionTicks,
            Func<long, CancellationToken, Task<bool>> sendSeek,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                throw new ArgumentException("sessionId is required", nameof(sessionId));
            }
            if (sendResume == null)
            {
                throw new ArgumentNullException(nameof(sendResume));
            }
            if (getTargetPositionTicks == null)
            {
                throw new ArgumentNullException(nameof(getTargetPositionTicks));
            }
            if (sendSeek == null)
            {
                throw new ArgumentNullException(nameof(sendSeek));
            }

            var registration = Register(sessionId, cancellationToken);
            try
            {
                var pipelineCompleted = false;
                var enqueued = _sessionQueue.TryEnqueue(
                    sessionId,
                    async queuedToken =>
                    {
                        if (!await sendResume(queuedToken).ConfigureAwait(false))
                        {
                            return;
                        }

                        queuedToken.ThrowIfCancellationRequested();
                        var targetPositionTicks = Math.Max(0, getTargetPositionTicks());
                        queuedToken.ThrowIfCancellationRequested();
                        pipelineCompleted = await sendSeek(
                            targetPositionTicks,
                            queuedToken).ConfigureAwait(false);
                    },
                    registration.CancellationToken,
                    out var completion);
                if (!enqueued)
                {
                    throw new InvalidOperationException(
                        $"Resume pipeline queue is full for session {sessionId}");
                }

                await completion.ConfigureAwait(false);
                return pipelineCompleted;
            }
            catch (OperationCanceledException)
                when (registration.CancellationToken.IsCancellationRequested)
            {
                return false;
            }
            finally
            {
                Complete(registration);
            }
        }

        public void CancelSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return;
            }

            HashSet<PipelineRegistration> registrations;
            lock (_syncRoot)
            {
                if (!_registrations.TryGetValue(sessionId, out registrations))
                {
                    return;
                }

                _registrations.Remove(sessionId);
            }

            foreach (var registration in registrations)
            {
                registration.Cancel();
            }
        }

        public void Clear()
        {
            var registrationsToCancel = new List<PipelineRegistration>();
            lock (_syncRoot)
            {
                foreach (var registrations in _registrations.Values)
                {
                    registrationsToCancel.AddRange(registrations);
                }

                _registrations.Clear();
            }

            foreach (var registration in registrationsToCancel)
            {
                registration.Cancel();
            }
        }

        private PipelineRegistration Register(
            string sessionId,
            CancellationToken cancellationToken)
        {
            var registration = new PipelineRegistration(
                sessionId,
                cancellationToken);
            lock (_syncRoot)
            {
                if (!_registrations.TryGetValue(sessionId, out var registrations))
                {
                    registrations = new HashSet<PipelineRegistration>();
                    _registrations[sessionId] = registrations;
                }

                registrations.Add(registration);
            }

            return registration;
        }

        private void Complete(PipelineRegistration registration)
        {
            lock (_syncRoot)
            {
                if (_registrations.TryGetValue(
                        registration.SessionId,
                        out var registrations))
                {
                    registrations.Remove(registration);
                    if (registrations.Count == 0)
                    {
                        _registrations.Remove(registration.SessionId);
                    }
                }
            }

            registration.Dispose();
        }

        private sealed class PipelineRegistration : IDisposable
        {
            private readonly object _syncRoot = new object();
            private readonly CancellationTokenSource _cancellation;
            private bool _disposed;

            public PipelineRegistration(
                string sessionId,
                CancellationToken cancellationToken)
            {
                SessionId = sessionId;
                _cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            }

            public string SessionId { get; }

            public CancellationToken CancellationToken => _cancellation.Token;

            public void Cancel()
            {
                lock (_syncRoot)
                {
                    if (!_disposed && !_cancellation.IsCancellationRequested)
                    {
                        _cancellation.Cancel();
                    }
                }
            }

            public void Dispose()
            {
                lock (_syncRoot)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _disposed = true;
                    _cancellation.Dispose();
                }
            }
        }
    }
}
