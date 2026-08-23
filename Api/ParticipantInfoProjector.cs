using System;
using System.Collections.Generic;
using System.Linq;

namespace WatchPartyForEmby.Api
{
    public sealed class ParticipantSessionDescriptor
    {
        public string SessionId { get; set; }
        public string Client { get; set; }
        public bool SupportsRemoteControl { get; set; }
        public bool IsOnline { get; set; }
        public bool HasActiveWebSocket { get; set; }
        public bool IsDormant { get; set; }
        public bool CanReceiveCommands { get; set; }
    }

    /// <summary>
    /// Projects runtime playback sessions without collapsing devices that share an
    /// Emby user account. The master role belongs to one SessionId, not to the user.
    /// </summary>
    public static class ParticipantInfoProjector
    {
        public static List<ParticipantInfo> Project(
            IEnumerable<PartyParticipant> participants,
            IEnumerable<string> readyUserIds,
            string masterSessionId,
            IEnumerable<ParticipantSessionDescriptor> sessionDescriptors)
        {
            var readyUsers = new HashSet<string>(
                readyUserIds ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            var descriptors = (sessionDescriptors ?? Array.Empty<ParticipantSessionDescriptor>())
                .Where(descriptor => !string.IsNullOrEmpty(descriptor?.SessionId))
                .GroupBy(descriptor => descriptor.SessionId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            return (participants ?? Array.Empty<PartyParticipant>())
                .Where(participant => participant != null)
                .Select(participant =>
                {
                    descriptors.TryGetValue(participant.SessionId ?? string.Empty, out var descriptor);
                    return new ParticipantInfo
                    {
                        UserId = participant.UserId,
                        UserName = participant.UserName,
                        SessionId = participant.SessionId,
                        Client = descriptor?.Client ?? string.Empty,
                        SupportsRemoteControl = descriptor?.SupportsRemoteControl == true,
                        IsOnline = descriptor?.IsOnline == true,
                        HasActiveWebSocket = descriptor?.HasActiveWebSocket == true,
                        IsDormant = descriptor?.IsDormant == true,
                        CanReceiveCommands = descriptor?.CanReceiveCommands == true,
                        IsHost = !string.IsNullOrEmpty(masterSessionId)
                            && string.Equals(
                                participant.SessionId,
                                masterSessionId,
                                StringComparison.Ordinal),
                        IsReady = !string.IsNullOrEmpty(participant.UserId)
                            && readyUsers.Contains(participant.UserId),
                        IsBuffering = participant.IsBuffering,
                        IsPaused = participant.IsPaused,
                        CurrentPositionTicks = participant.CurrentPositionTicks,
                        LastActivityAt = participant.LastActivityAt
                    };
                })
                .ToList();
        }
    }
}
