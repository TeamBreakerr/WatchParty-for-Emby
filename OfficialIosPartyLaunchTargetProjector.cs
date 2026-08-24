using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Session;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Projects Emby's online official-iOS sessions into the single launch-target
    /// model consumed by both the API and the embedded control page.
    /// </summary>
    public static class OfficialIosPartyLaunchTargetProjector
    {
        public static IReadOnlyList<PartyLaunchTarget> Project(
            IEnumerable<SessionInfo> sessions,
            string masterSessionId,
            Func<SessionInfo, bool> canJoin,
            Func<string, bool> isInRoom)
        {
            if (canJoin == null)
            {
                throw new ArgumentNullException(nameof(canJoin));
            }
            if (isInRoom == null)
            {
                throw new ArgumentNullException(nameof(isInRoom));
            }

            return OfficialIosPartySessionDiscovery.Discover(sessions)
                .Select(session => BuildEvaluatedTarget(
                    session,
                    masterSessionId,
                    canJoin(session),
                    isInRoom(session.Id)))
                .OrderByDescending(target => target.CanLaunch)
                .ThenBy(target => target.UserName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(target => target.DeviceName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static PartyLaunchTarget BuildEvaluatedTarget(
            SessionInfo session,
            string masterSessionId,
            bool canJoin,
            bool inRoom)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            var isMaster = string.Equals(
                session.Id,
                masterSessionId,
                StringComparison.Ordinal);
            var supportsRemoteControl =
                PlaybackControlCapabilities.CanReceivePlaybackCommand(
                    session.SupportsRemoteControl,
                    session.PlayableMediaTypes);
            var hasActiveWebSocket =
                OfficialIosWebSocketTransport.HasActiveWebSocketController(
                    session.SessionControllers);
            var facts = new PartyLaunchTargetFacts
            {
                IsMaster = isMaster,
                CanJoin = canJoin,
                SupportsRemoteControl = supportsRemoteControl,
                HasActiveWebSocket = hasActiveWebSocket,
                InRoom = inRoom
            };
            var eligibility = PartyLaunchTargetEligibility.Decide(facts);

            return new PartyLaunchTarget
            {
                SessionId = session.Id,
                UserId = session.UserId,
                UserName = session.UserName ?? session.UserId,
                DeviceName = session.DeviceName ?? string.Empty,
                Client = session.Client ?? string.Empty,
                IsMaster = isMaster,
                InRoom = inRoom,
                SupportsRemoteControl = supportsRemoteControl,
                HasActiveWebSocket = hasActiveWebSocket,
                CanLaunch = eligibility.CanLaunch,
                Message = eligibility.Message
            };
        }
    }
}
