namespace WatchPartyForEmby
{
    /// <summary>
    /// Describes an operator-triggered current-episode selection. Accepted means the
    /// room queue/state was updated; command targets are only the online sessions for
    /// which the server started a generation-bound PlayNow handoff.
    /// </summary>
    public sealed class PartyEpisodeSelectionResult
    {
        public bool Accepted { get; set; }
        public string Message { get; set; }
        public string EpisodeItemId { get; set; }
        public string EpisodeName { get; set; }
        public int EpisodeIndex { get; set; } = -1;
        public int CommandTargetCount { get; set; }
    }
}
