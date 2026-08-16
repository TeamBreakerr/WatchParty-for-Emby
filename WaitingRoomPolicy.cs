using System;
using System.Collections.Generic;

namespace WatchPartyForEmby
{
    public static class WaitingRoomPolicy
    {
        public static bool ShouldAutoStart(WatchPartyItem party, int readyUserCount)
        {
            return party != null
                && party.IsWaitingRoom
                && !party.IsPlaying
                && party.AutoStartWhenReady
                && readyUserCount >= party.MinReadyCount;
        }

        public static bool SuppressesPlaybackControl(WatchPartyItem party)
        {
            return party?.IsWaitingRoom == true;
        }

        public static bool ShouldRestorePause(
            WatchPartyItem party,
            bool reportedIsPaused)
        {
            return SuppressesPlaybackControl(party) && !reportedIsPaused;
        }

        public static int CountPresentReadyUsers(
            IEnumerable<string> readyUserIds,
            IEnumerable<string> presentUserIds)
        {
            var presentUsers = new HashSet<string>(
                presentUserIds ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            var countedReadyUsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (readyUserIds != null)
            {
                foreach (var readyUserId in readyUserIds)
                {
                    if (!string.IsNullOrWhiteSpace(readyUserId)
                        && presentUsers.Contains(readyUserId))
                    {
                        countedReadyUsers.Add(readyUserId);
                    }
                }
            }

            return countedReadyUsers.Count;
        }
    }
}
