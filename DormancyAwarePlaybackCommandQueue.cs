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

        public DormancyAwarePlaybackCommandQueue(
            ParticipantDormancyTracker dormancies,
            int capacityPerSession)
        {
            _dormancies = dormancies
                ?? throw new ArgumentNullException(nameof(dormancies));
            _queue = new KeyedAsyncSerialQueue<string>(capacityPerSession);
        }

        public async Task<bool> EnqueueAsync(
            string partyId,
            string sessionId,
            ParticipantRoomCommand command,
            ParticipantCommandQueueMode mode,
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(partyId) || string.IsNullOrEmpty(sessionId))
            {
                throw new ArgumentException("partyId and sessionId are required");
            }
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }
            if (!_dormancies.CanReceiveCommand(partyId, sessionId, command))
            {
                return false;
            }

            var commandSent = false;
            Func<CancellationToken, Task> guardedOperation = async queuedToken =>
            {
                if (!_dormancies.CanReceiveCommand(partyId, sessionId, command))
                {
                    return;
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
