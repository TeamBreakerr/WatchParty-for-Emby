namespace WatchPartyForEmby
{
    /// <summary>
    /// Describes one online official iOS session that can be shown as a manual
    /// PlayNow target. Eligibility is advisory here and is always revalidated when
    /// the command is submitted.
    /// </summary>
    public sealed class PartyLaunchTarget
    {
        public string SessionId { get; set; }
        public string UserId { get; set; }
        public string UserName { get; set; }
        public string DeviceName { get; set; }
        public string Client { get; set; }
        public bool IsMaster { get; set; }
        public bool InRoom { get; set; }
        public bool SupportsRemoteControl { get; set; }
        public bool HasActiveWebSocket { get; set; }
        public bool CanLaunch { get; set; }
        public string Message { get; set; }
    }

    public sealed class PartyLaunchTargetEligibility
    {
        public bool CanLaunch { get; set; }
        public string Message { get; set; }

        public static PartyLaunchTargetEligibility Decide(
            PartyLaunchTargetFacts facts)
        {
            if (facts == null)
            {
                throw new System.ArgumentNullException(nameof(facts));
            }
            if (facts.IsMaster)
            {
                return Denied("当前 Master Session");
            }
            if (!facts.CanJoin)
            {
                return Denied("不符合房间加入条件");
            }
            if (!facts.SupportsRemoteControl)
            {
                return Denied("客户端未声明远程播放能力");
            }
            if (!facts.HasActiveWebSocket)
            {
                return Denied("在线，但控制连接未建立");
            }

            return new PartyLaunchTargetEligibility
            {
                CanLaunch = true,
                Message = facts.InRoom ? "在线 · 已在房间" : "在线 · 可拉入房间"
            };
        }

        private static PartyLaunchTargetEligibility Denied(string message)
        {
            return new PartyLaunchTargetEligibility
            {
                CanLaunch = false,
                Message = message
            };
        }
    }

    public sealed class PartyLaunchTargetFacts
    {
        public bool IsMaster { get; set; }
        public bool CanJoin { get; set; }
        public bool SupportsRemoteControl { get; set; }
        public bool HasActiveWebSocket { get; set; }
        public bool InRoom { get; set; }
    }
}
