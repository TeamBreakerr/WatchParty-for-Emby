using System;
using System.Linq;

namespace WatchPartyForEmby
{
    public static class WatchPartyAuthorizationPolicy
    {
        public static bool CanAccessParty(
            WatchPartyItem party,
            string userId,
            bool isAdministrator)
        {
            if (party == null || string.IsNullOrWhiteSpace(userId))
            {
                return false;
            }

            if (isAdministrator
                || IsSameUser(party.MasterUserId, userId)
                || IsSameUser(party.HostUserId, userId))
            {
                return true;
            }

            return party.AllowedUserIds == null
                || party.AllowedUserIds.Count == 0
                || party.AllowedUserIds.Any(allowed => IsSameUser(allowed, userId));
        }

        public static bool CanStartParty(
            WatchPartyItem party,
            string userId,
            bool isAdministrator)
        {
            return party != null
                && !string.IsNullOrWhiteSpace(userId)
                && (isAdministrator
                    || IsSameUser(party.MasterUserId, userId)
                    || IsSameUser(party.HostUserId, userId));
        }

        public static bool CanSetReady(
            WatchPartyItem party,
            string userId,
            bool isAdministrator,
            bool hasActivePartySession)
        {
            return hasActivePartySession
                && CanAccessParty(party, userId, isAdministrator);
        }

        public static bool CanSynchronizeParty(
            WatchPartyItem party,
            string userId,
            bool isAdministrator)
        {
            return CanStartParty(party, userId, isAdministrator);
        }

        private static bool IsSameUser(string left, string right)
        {
            return !string.IsNullOrWhiteSpace(left)
                && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
