using System;
using System.Threading.Tasks;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Routes an authority decision to exactly one command scope. Participant
    /// divergence can only restore the reporting session; only master transitions
    /// can enter a room-wide broadcast path.
    /// </summary>
    public static class PlaybackStateAuthorityDispatcher
    {
        public static Task Dispatch(
            PlaybackStateAuthorityAction action,
            bool reportedIsPaused,
            Func<Task> restoreReportingParticipant,
            Func<Task> broadcastMasterPause,
            Func<Task> broadcastMasterResume)
        {
            if (restoreReportingParticipant == null)
            {
                throw new ArgumentNullException(nameof(restoreReportingParticipant));
            }
            if (broadcastMasterPause == null)
            {
                throw new ArgumentNullException(nameof(broadcastMasterPause));
            }
            if (broadcastMasterResume == null)
            {
                throw new ArgumentNullException(nameof(broadcastMasterResume));
            }

            switch (action)
            {
                case PlaybackStateAuthorityAction.Ignore:
                    return Task.CompletedTask;
                case PlaybackStateAuthorityAction.RestoreParticipant:
                    return restoreReportingParticipant();
                case PlaybackStateAuthorityAction.BroadcastMaster:
                    return reportedIsPaused
                        ? broadcastMasterPause()
                        : broadcastMasterResume();
                default:
                    throw new ArgumentOutOfRangeException(nameof(action), action, null);
            }
        }
    }
}
