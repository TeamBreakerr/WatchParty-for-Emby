using System;
using System.Threading;
using System.Threading.Tasks;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Retries an acknowledged playback-state command without sending another copy
    /// when the client acknowledgement arrives during the retry wait.
    /// </summary>
    public sealed class AcknowledgedPlaybackStateCommandRetrier
    {
        private readonly int _maxAttempts;
        private readonly TimeSpan _retryDelay;
        private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

        public AcknowledgedPlaybackStateCommandRetrier(
            int maxAttempts,
            TimeSpan retryDelay,
            Func<TimeSpan, CancellationToken, Task> delayAsync = null)
        {
            if (maxAttempts <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxAttempts));
            }
            if (retryDelay < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(retryDelay));
            }

            _maxAttempts = maxAttempts;
            _retryDelay = retryDelay;
            _delayAsync = delayAsync ?? Task.Delay;
        }

        public async Task<bool> SendAsync(
            Func<CancellationToken, Task<bool>> sendCommand,
            Func<bool> acknowledgementPending,
            Action<int, Exception> onAttemptFailed,
            Action<int> onAcknowledgementMissing,
            CancellationToken cancellationToken)
        {
            if (sendCommand == null)
            {
                throw new ArgumentNullException(nameof(sendCommand));
            }
            if (acknowledgementPending == null)
            {
                throw new ArgumentNullException(nameof(acknowledgementPending));
            }

            for (var attempt = 1; attempt <= _maxAttempts; attempt++)
            {
                try
                {
                    if (!await sendCommand(cancellationToken).ConfigureAwait(false))
                    {
                        return false;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (!acknowledgementPending())
                    {
                        return true;
                    }
                    if (attempt == _maxAttempts)
                    {
                        throw;
                    }

                    onAttemptFailed?.Invoke(attempt, ex);
                    await _delayAsync(_retryDelay, cancellationToken).ConfigureAwait(false);
                    if (!acknowledgementPending())
                    {
                        return true;
                    }
                    continue;
                }

                if (attempt == _maxAttempts || !acknowledgementPending())
                {
                    return true;
                }

                onAcknowledgementMissing?.Invoke(attempt + 1);
                await _delayAsync(_retryDelay, cancellationToken).ConfigureAwait(false);
                if (!acknowledgementPending())
                {
                    return true;
                }
            }

            return true;
        }
    }
}
