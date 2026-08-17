using System;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Projects a participant's last reported position to the time of a periodic sync
    /// check. Emby clients commonly report every 5-10 seconds; comparing that cached
    /// value directly with the live master clock creates false drift and seek storms.
    /// </summary>
    public static class ParticipantPositionEstimator
    {
        public static long Estimate(
            PartyParticipant participant,
            long reportedPositionTicks,
            bool isPaused,
            DateTime nowUtc,
            TimeSpan maxProjection)
        {
            reportedPositionTicks = Math.Max(0, reportedPositionTicks);
            if (participant == null
                || isPaused
                || participant.IsPaused != isPaused
                || participant.CurrentPositionTicks != reportedPositionTicks
                || participant.LastActivityAt <= DateTime.MinValue
                || nowUtc <= participant.LastActivityAt)
            {
                return reportedPositionTicks;
            }

            var elapsed = nowUtc - participant.LastActivityAt;
            if (maxProjection < TimeSpan.Zero)
            {
                maxProjection = TimeSpan.Zero;
            }
            if (elapsed > maxProjection)
            {
                elapsed = maxProjection;
            }

            return reportedPositionTicks + elapsed.Ticks;
        }

        public static bool IsNewPlaybackReport(
            PartyParticipant participant,
            long reportedPositionTicks,
            bool isPaused)
        {
            return participant == null
                || participant.CurrentPositionTicks != Math.Max(0, reportedPositionTicks)
                || participant.IsPaused != isPaused;
        }
    }
}
