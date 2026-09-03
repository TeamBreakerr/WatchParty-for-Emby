using System;
using System.Threading;
using System.Threading.Tasks;

namespace WatchPartyForEmby
{
    public enum ParticipantCommandQueueMode
    {
        Ordered,
        Latest
    }

    /// <summary>
    /// Serializes remote playback commands per Emby SessionId and applies dormancy at
    /// both submission and execution time. The second check closes the race where Stop
    /// arrives while an older command is still waiting behind another queued command.
    /// </summary>
    public sealed class DormancyAwarePlaybackCommandQueue
    {
        private readonly KeyedAsyncSerialQueue<string> _queue;
        private readonly ParticipantDormancyTracker _dormancies;
        private readonly ProviderBurstPacer _providerBursts;

        public DormancyAwarePlaybackCommandQueue(
            ParticipantDormancyTracker dormancies,
            int capacityPerSession,
            ProviderBurstPacer providerBursts = null)
        {
            _dormancies = dormancies
                ?? throw new ArgumentNullException(nameof(dormancies));
            _queue = new KeyedAsyncSerialQueue<string>(capacityPerSession);
            _providerBursts = providerBursts;
        }

        public async Task<bool> EnqueueAsync(
            string partyId,
            string sessionId,
            ParticipantRoomCommand command,
            ParticipantCommandQueueMode mode,
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken)
        {
            return await EnqueueCoreAsync(
                partyId,
                sessionId,
                command,
                mode,
                operation,
                cancellationToken,
                allowDormant: false,
                authorizationStillValid: null).ConfigureAwait(false);
        }

        public async Task<bool> EnqueueExplicitPlayNowAsync(
            string partyId,
            string sessionId,
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken,
            Func<bool> authorizationStillValid = null)
        {
            return await EnqueueCoreAsync(
                partyId,
                sessionId,
                ParticipantRoomCommand.PlayNow,
                ParticipantCommandQueueMode.Ordered,
                operation,
                cancellationToken,
                allowDormant: true,
                authorizationStillValid).ConfigureAwait(false);
        }

        private async Task<bool> EnqueueCoreAsync(
            string partyId,
            string sessionId,
            ParticipantRoomCommand command,
            ParticipantCommandQueueMode mode,
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken,
            bool allowDormant,
            Func<bool> authorizationStillValid)
        {
            if (string.IsNullOrEmpty(partyId) || string.IsNullOrEmpty(sessionId))
            {
                throw new ArgumentException("partyId and sessionId are required");
            }
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }
            if (!allowDormant
                && !_dormancies.CanReceiveCommand(partyId, sessionId, command))
            {
                return false;
            }
            if (authorizationStillValid != null && !authorizationStillValid())
            {
                return false;
            }

            var commandSent = false;
            bool StillDispatchable()
            {
                return (allowDormant
                        || _dormancies.CanReceiveCommand(partyId, sessionId, command))
                    && (authorizationStillValid == null || authorizationStillValid());
            }

            Func<CancellationToken, Task> guardedOperation = async queuedToken =>
            {
                if (!StillDispatchable())
                {
                    return;
                }

                if (_providerBursts != null)
                {
                    // Participants of one party act together only because the server
                    // told them to, and the provider rejects a third read that starts
                    // with the other two. Spacing the fan-out is the only place that
                    // can prevent an error a direct-playing client cannot retry.
                    await _providerBursts
                        .PaceAsync(partyId, command, queuedToken)
                        .ConfigureAwait(false);
                    // Stop can arrive while a command is spacing itself out.
                    if (!StillDispatchable())
                    {
                        return;
                    }
                }

                await operation(queuedToken).ConfigureAwait(false);
                commandSent = true;
            };

            Task completion;
            var enqueued = mode == ParticipantCommandQueueMode.Latest
                ? _queue.TryEnqueueLatest(
                    sessionId,
                    command,
                    guardedOperation,
                    cancellationToken,
                    out completion)
                : _queue.TryEnqueue(
                    sessionId,
                    guardedOperation,
                    cancellationToken,
                    out completion);
            if (!enqueued)
            {
                throw new InvalidOperationException(
                    $"Remote playback command queue is full for session {sessionId}");
            }

            await completion.ConfigureAwait(false);
            return commandSent;
        }
    }
}
