using System;
using System.Collections.Generic;
using System.Linq;

namespace WatchPartyForEmby
{
    public static class MasterSessionSelectionPolicy
    {
        public static bool CanTakeOver(
            string registeredMasterSessionId,
            string incomingSessionId,
            Func<string, bool> isPlayingCurrentParty)
        {
            if (string.IsNullOrEmpty(registeredMasterSessionId)
                || string.Equals(
                    registeredMasterSessionId,
                    incomingSessionId,
                    StringComparison.Ordinal))
            {
                return true;
            }

            return isPlayingCurrentParty == null
                || !isPlayingCurrentParty(registeredMasterSessionId);
        }

        public static string SelectLatestActiveSession(
            IEnumerable<PartyParticipant> participants,
            string userId,
            Func<string, bool> isPlayingCurrentParty)
        {
            if (string.IsNullOrWhiteSpace(userId) || isPlayingCurrentParty == null)
            {
                return null;
            }

            return (participants ?? Array.Empty<PartyParticipant>())
                .Where(participant => participant != null
                    && !string.IsNullOrEmpty(participant.SessionId)
                    && string.Equals(
                        participant.UserId,
                        userId,
                        StringComparison.OrdinalIgnoreCase)
                    && isPlayingCurrentParty(participant.SessionId))
                .OrderByDescending(participant => participant.LastActivityAt)
                .Select(participant => participant.SessionId)
                .FirstOrDefault();
        }
    }
}
