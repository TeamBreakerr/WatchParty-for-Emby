using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace WatchPartyForEmby
{
    public class PartyParticipant
    {
        public string UserId { get; set; }
        public string UserName { get; set; }
        public string SessionId { get; set; }
        public string PlaySessionId { get; set; }
        public DateTime JoinedAt { get; set; }
        public DateTime LastActivityAt { get; set; }
        public long CurrentPositionTicks { get; set; }
        public bool IsPaused { get; set; }
        public bool IsBuffering { get; set; }
        
        public PartyParticipant()
        {
            JoinedAt = DateTime.UtcNow;
            LastActivityAt = DateTime.UtcNow;
        }
    }

    public class WatchPartyEpisode
    {
        public string ItemId { get; set; }
        /// <summary>
        /// The concrete Emby media-source identifier selected for this room. The
        /// logical ItemId remains the item used for party matching; this value makes
        /// PlayNow select the exact file/version instead of the server default.
        /// </summary>
        public string MediaSourceId { get; set; }
        public string ItemName { get; set; }
        public string SeasonId { get; set; }
        public int SeasonNumber { get; set; }
        public int EpisodeNumber { get; set; }
    }

    public class WatchPartyItem
    {
        public string Id { get; set; }
        public string LibraryId { get; set; }
        public string ItemId { get; set; }
        /// <summary>
        /// The concrete media source selected for a movie or single-episode room.
        /// Series rooms store a source on the corresponding queue episode instead.
        /// </summary>
        public string MediaSourceId { get; set; }
        public string ItemName { get; set; }
        public string ItemType { get; set; }
        public string SeriesId { get; set; }
        public string SeriesName { get; set; }
        public string SeasonId { get; set; }
        public bool IsSeriesParty { get; set; }
        public List<WatchPartyEpisode> EpisodeQueue { get; set; }
        public int CurrentEpisodeIndex { get; set; }
        public string CurrentEpisodeId { get; set; }
        public bool IsActive { get; set; }
        public long CurrentPositionTicks { get; set; }
        public bool IsPlaying { get; set; }
        public int MaxParticipants { get; set; }
        public DateTime CreatedDate { get; set; }
        
        public List<string> AllowedUserIds { get; set; }
        public string HostUserId { get; set; }
        public string MasterUserId { get; set; }
        public bool IsWaitingRoom { get; set; }
        public bool AutoStartWhenReady { get; set; }
        public int MinReadyCount { get; set; }
        public int SyncToleranceSeconds { get; set; }
        public int MaxBufferThresholdSeconds { get; set; }
        public bool AutoKickInactiveMinutes { get; set; }
        public int InactiveTimeoutMinutes { get; set; }

        public WatchPartyItem()
        {
            Id = Guid.NewGuid().ToString();
            IsActive = true;
            MaxParticipants = 50;
            CreatedDate = DateTime.UtcNow;
            AllowedUserIds = new List<string>();
            EpisodeQueue = new List<WatchPartyEpisode>();
            CurrentEpisodeIndex = -1;
            IsWaitingRoom = true;
            AutoStartWhenReady = true;
            MinReadyCount = 1;
            SyncToleranceSeconds = 2;
            MaxBufferThresholdSeconds = 30;
            AutoKickInactiveMinutes = true;
            InactiveTimeoutMinutes = 15;
        }
    }

    public class PluginConfiguration : BasePluginConfiguration
    {
        public int ConfigurationVersion { get; set; }
        public List<WatchPartyItem> WatchParties { get; set; }

        public int SyncIntervalSeconds { get; set; } = 5;
        // Retained only so older XML configurations deserialize cleanly. Runtime sync
        // uses per-session measured resume latency and always normalizes this legacy
        // fixed offset to zero.
        public int SyncOffsetMilliseconds { get; set; } = 0;
        public string DefaultMasterUserId { get; set; } = "";

        public PluginConfiguration()
        {
            ConfigurationVersion = 0;
            WatchParties = new List<WatchPartyItem>();
            SyncIntervalSeconds = 5;
            SyncOffsetMilliseconds = 0;
            DefaultMasterUserId = string.Empty;
        }
    }
}
