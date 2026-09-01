using System;
using System.Threading;
using System.Threading.Tasks;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Converges one participant on an authoritative paused position. Every attempt is
    /// ordered as Pause then Seek, and only a later client report that is both paused
    /// and at the commanded position may confirm completion.
    /// </summary>
    public sealed class ConfirmedParticipantPauseSynchronizer
    {
        private readonly ConfirmedPlaybackSeekRetrier _retrier;

        public ConfirmedParticipantPauseSynchronizer(
            int maxAttempts,
            TimeSpan confirmationTimeout,
            Func<TimeSpan, CancellationToken, Task> delayAsync = null)
        {
            _retrier = new ConfirmedPlaybackSeekRetrier(
                maxAttempts,
                confirmationTimeout,
                delayAsync);
        }

        public Task<bool> SendAsync(
            string sessionId,
            long targetPositionTicks,
            Func<CancellationToken, Task<bool>> sendPause,
            Func<long, CancellationToken, Task<bool>> sendSeek,
            CancellationToken cancellationToken)
        {
            if (sendPause == null)
            {
                throw new ArgumentNullException(nameof(sendPause));
            }
            if (sendSeek == null)
            {
                throw new ArgumentNullException(nameof(sendSeek));
            }

            return _retrier.SendAsync(
                sessionId,
                targetPositionTicks,
                () => targetPositionTicks,
                async (target, attemptToken) =>
                {
                    if (!await sendPause(attemptToken).ConfigureAwait(false))
                    {
                        return false;
                    }

                    attemptToken.ThrowIfCancellationRequested();
                    return await sendSeek(target, attemptToken).ConfigureAwait(false);
                },
                cancellationToken);
        }

        public bool Confirm(string sessionId)
        {
            return _retrier.Confirm(sessionId);
        }
    }
}
