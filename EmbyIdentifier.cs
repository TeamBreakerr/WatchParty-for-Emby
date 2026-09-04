using System;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Emby carries one identity in two shapes. <c>SessionInfo.UserId</c> is the
    /// 32-character form, while <c>User.Id.ToString()</c> inserts hyphens, and the
    /// browser reports whichever shape its own API client holds. Comparing those
    /// ordinally never matches: the explicit Web seek endpoint resolved no session at
    /// all, so every drag fell back to the heartbeat inference that endpoint exists to
    /// replace - the path that misreads a buffering master as a fresh drag and
    /// re-seeks every participant.
    /// </summary>
    public static class EmbyIdentifier
    {
        /// <summary>
        /// Compares two Emby identifiers by identity rather than by spelling. Equal
        /// text still matches, so a non-GUID identifier keeps working unchanged.
        /// </summary>
        public static bool Matches(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return Guid.TryParse(left, out var leftId)
                && Guid.TryParse(right, out var rightId)
                && leftId == rightId;
        }
    }
}
