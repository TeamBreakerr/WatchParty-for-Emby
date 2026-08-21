using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Sends a playback seek and allows one bounded retry when the client never reports
    /// that it reached the commanded position.
    /// </summary>
    public sealed class ConfirmedPlaybackSeekRetrier
    {
        private readonly int _maxAttempts;
        private readonly TimeSpan _confirmationTimeout;
        private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
        private readonly ConcurrentDictionary<string, SeekConfirmation> _confirmations =
            new ConcurrentDictionary<string, SeekConfirmation>(StringComparer.Ordinal);

        public ConfirmedPlaybackSeekRetrier(
            int maxAttempts,
            TimeSpan confirmationTimeout,
            Func<TimeSpan, CancellationToken, Task> delayAsync = null)
        {
            if (maxAttempts <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxAttempts));
            }
            if (confirmationTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(confirmationTimeout));
            }

            _maxAttempts = maxAttempts;
            _confirmationTimeout = confirmationTimeout;
            _delayAsync = delayAsync ?? Task.Delay;
        }

        public async Task<bool> SendAsync(
            string sessionId,
            long initialTargetPositionTicks,
            Func<long> getRetryTargetPositionTicks,
            Func<long, CancellationToken, Task<bool>> sendSeek,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                throw new ArgumentException("sessionId is required", nameof(sessionId));
            }
            if (initialTargetPositionTicks < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(initialTargetPositionTicks));
            }
            if (getRetryTargetPositionTicks == null)
            {
                throw new ArgumentNullException(nameof(getRetryTargetPositionTicks));
            }
            if (sendSeek == null)
            {
                throw new ArgumentNullException(nameof(sendSeek));
            }

            var confirmation = new SeekConfirmation();
            if (!_confirmations.TryAdd(sessionId, confirmation))
            {
                throw new InvalidOperationException(
                    $"A confirmed seek is already active for session {sessionId}");
            }

            try
            {
                for (var attempt = 1; attempt <= _maxAttempts; attempt++)
                {
                    if (confirmation.Task.IsCompleted)
                    {
                        return true;
                    }

                    var targetPositionTicks = attempt == 1
                        ? initialTargetPositionTicks
                        : Math.Max(0, getRetryTargetPositionTicks());
                    if (!await sendSeek(targetPositionTicks, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        return false;
                    }

                    if (confirmation.Task.IsCompleted)
                    {
                        return true;
                    }

                    var timeoutTask = _delayAsync(
                        _confirmationTimeout,
                        cancellationToken);
                    var completed = await Task.WhenAny(
                        confirmation.Task,
                        timeoutTask).ConfigureAwait(false);
                    if (completed == confirmation.Task)
                    {
                        return true;
                    }

                    await timeoutTask.ConfigureAwait(false);
                }

                return confirmation.Task.IsCompleted;
            }
            finally
            {
                if (_confirmations.TryGetValue(sessionId, out var current)
                    && ReferenceEquals(current, confirmation))
                {
                    _confirmations.TryRemove(sessionId, out _);
                }
            }
        }

        public bool Confirm(string sessionId)
        {
            return !string.IsNullOrEmpty(sessionId)
                && _confirmations.TryGetValue(sessionId, out var confirmation)
                && confirmation.TryConfirm();
        }

        private sealed class SeekConfirmation
        {
            private readonly TaskCompletionSource<bool> _completion =
                new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<bool> Task => _completion.Task;

            public bool TryConfirm()
            {
                return _completion.TrySetResult(true);
            }
        }
    }
}
