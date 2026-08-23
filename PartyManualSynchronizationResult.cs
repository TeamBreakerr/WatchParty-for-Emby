using System.Collections.Generic;

namespace WatchPartyForEmby
{
    public sealed class PartyManualSynchronizationResult
    {
        public bool Accepted { get; set; }
        public string Message { get; set; }
        public List<ParticipantManualSynchronizationResult> Participants { get; set; } =
            new List<ParticipantManualSynchronizationResult>();
    }

    public sealed class ParticipantManualSynchronizationResult
    {
        public string SessionId { get; set; }
        public string UserName { get; set; }
        public string Client { get; set; }
        public bool Online { get; set; }
        public bool HasControlConnection { get; set; }
        public bool CommandSent { get; set; }
        public string Message { get; set; }
    }
}
