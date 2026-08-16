using System;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class MasterSessionSelectionPolicyTests
    {
        [Fact]
        public void RegisteredSessionPlayingUnrelatedContentDoesNotBlockTakeover()
        {
            Assert.True(MasterSessionSelectionPolicy.CanTakeOver(
                "old-session",
                "new-session",
                sessionId => sessionId != "old-session"));
            Assert.False(MasterSessionSelectionPolicy.CanTakeOver(
                "old-session",
                "new-session",
                sessionId => sessionId == "old-session"));
        }

        [Fact]
        public void PromotionChoosesLatestSessionActuallyPlayingTheParty()
        {
            var participants = new[]
            {
                Participant("unrelated", 30),
                Participant("party-old", 10),
                Participant("party-new", 20)
            };

            var selected = MasterSessionSelectionPolicy.SelectLatestActiveSession(
                participants,
                "master-user",
                sessionId => sessionId.StartsWith("party-", StringComparison.Ordinal));

            Assert.Equal("party-new", selected);
        }

        private static PartyParticipant Participant(string sessionId, int second)
        {
            return new PartyParticipant
            {
                UserId = "master-user",
                SessionId = sessionId,
                LastActivityAt = new DateTime(2026, 8, 17, 2, 0, second, DateTimeKind.Utc)
            };
        }
    }
}
