using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Session;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Projects every live, remotely controllable Emby session into the launch-target
    /// model consumed by the API and the embedded configuration page.
    /// </summary>
    public static class PartyLaunchTargetProjector
    {
        public static IReadOnlyList<PartyLaunchTarget> Project(
            IEnumerable<SessionInfo> sessions,
            string masterSessionId,
            Func<SessionInfo, bool> canJoin,
            Func<string, bool> isInRoom)
        {
            return Project(
                sessions,
                masterSessionId,
                masterUserId: null,
                canJoin,
                isInRoom,
                DateTime.UtcNow);
        }

        public static IReadOnlyList<PartyLaunchTarget> Project(
            IEnumerable<SessionInfo> sessions,
            string masterSessionId,
            string masterUserId,
            Func<SessionInfo, bool> canJoin,
            Func<string, bool> isInRoom,
            DateTime nowUtc)
        {
            if (canJoin == null)
            {
                throw new ArgumentNullException(nameof(canJoin));
            }
            if (isInRoom == null)
            {
                throw new ArgumentNullException(nameof(isInRoom));
            }

            return PartySessionDiscovery.Discover(sessions, nowUtc)
                .Select(session => BuildEvaluatedTarget(
                    session,
                    masterSessionId,
                    masterUserId,
                    canJoin(session),
                    isInRoom(session.Id),
                    nowUtc))
                .OrderByDescending(target => target.IsMaster)
                .ThenByDescending(target => target.CanLaunch)
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
            return BuildEvaluatedTarget(
                session,
                masterSessionId,
                masterUserId: null,
                canJoin,
                inRoom,
                DateTime.UtcNow);
        }

        public static PartyLaunchTarget BuildEvaluatedTarget(
            SessionInfo session,
            string masterSessionId,
            string masterUserId,
            bool canJoin,
            bool inRoom,
            DateTime nowUtc)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            var isMaster = string.Equals(
                session.Id,
                masterSessionId,
                StringComparison.Ordinal)
                || (string.IsNullOrEmpty(masterSessionId)
                    && string.Equals(
                        session.UserId,
                        masterUserId,
                        StringComparison.OrdinalIgnoreCase));
            var activeWebSocketControllerCount =
                OfficialIosWebSocketTransport.CountActiveWebSocketControllers(
                    session.SessionControllers);
            var hasActiveWebSocket = activeWebSocketControllerCount > 0;
            var isOfficialIos =
                OfficialIosWebSocketTransport.IsOfficialIosClient(session.Client);
            var hasAmbiguousWebControllers =
                PartySessionLivenessPolicy.IsWebClient(session.Client)
                && activeWebSocketControllerCount > 1;
            var isOnline = PartySessionLivenessPolicy.IsOnline(session, nowUtc);
            // Keep the projected facts internally consistent if the controller closes
            // between discovery and projection. The next poll will remove the target.
            if (isOfficialIos && !hasActiveWebSocket)
            {
                isOnline = false;
            }
            var supportsRemoteControl =
                PlaybackControlCapabilities.CanReceivePlaybackCommand(
                    PlaybackControlCapabilities.SessionSupportsRemoteControl(session),
                    session.PlayableMediaTypes);
            var facts = new PartyLaunchTargetFacts
            {
                IsMaster = isMaster,
                IsOnline = isOnline,
                CanJoin = canJoin,
                SupportsRemoteControl = supportsRemoteControl,
                InRoom = inRoom,
                ActiveControllerCount = activeWebSocketControllerCount,
                HasAmbiguousWebControllers = hasAmbiguousWebControllers
            };
            var eligibility = PartyLaunchTargetEligibility.Decide(facts);

            return new PartyLaunchTarget
            {
                SessionId = session.Id,
                UserId = session.UserId,
                UserName = session.UserName ?? session.UserId,
                DeviceName = session.DeviceName ?? string.Empty,
                Client = session.Client ?? string.Empty,
                IsOnline = isOnline,
                IsMaster = isMaster,
                InRoom = inRoom,
                SupportsRemoteControl = supportsRemoteControl,
                HasActiveWebSocket = hasActiveWebSocket,
                ActiveControllerCount = activeWebSocketControllerCount,
                HasAmbiguousWebControllers = hasAmbiguousWebControllers,
                CanLaunch = eligibility.CanLaunch,
                Message = eligibility.Message
            };
        }
    }
}
