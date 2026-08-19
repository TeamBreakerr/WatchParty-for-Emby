using System;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class ParticipantReconnectionPolicyTests
    {
        [Fact]
        public void NormalProgressIntervalDoesNotTriggerReconciliation()
        {
            var lastActivity = new DateTime(2026, 8, 19, 1, 0, 0, DateTimeKind.Utc);

            Assert.False(ParticipantReconnectionPolicy.RequiresResync(
                lastActivity,
                lastActivity.AddSeconds(10),
                TimeSpan.FromSeconds(20)));
        }

        [Fact]
        public void LongProgressGapTriggersReconciliation()
        {
            var lastActivity = new DateTime(2026, 8, 19, 1, 0, 0, DateTimeKind.Utc);

            Assert.True(ParticipantReconnectionPolicy.RequiresResync(
                lastActivity,
                lastActivity.AddSeconds(20),
                TimeSpan.FromSeconds(20)));
            Assert.True(ParticipantReconnectionPolicy.RequiresResync(
                lastActivity,
                lastActivity.AddMinutes(2),
                TimeSpan.FromSeconds(20)));
        }

        [Fact]
        public void InvalidOrBackwardsClockDoesNotTriggerReconciliation()
        {
            var lastActivity = new DateTime(2026, 8, 19, 1, 0, 0, DateTimeKind.Utc);

            Assert.False(ParticipantReconnectionPolicy.RequiresResync(
                DateTime.MinValue,
                lastActivity.AddSeconds(30),
                TimeSpan.FromSeconds(20)));
            Assert.False(ParticipantReconnectionPolicy.RequiresResync(
                lastActivity,
                lastActivity,
                TimeSpan.FromSeconds(20)));
            Assert.False(ParticipantReconnectionPolicy.RequiresResync(
                lastActivity,
                lastActivity.AddSeconds(-1),
                TimeSpan.FromSeconds(20)));
        }
    }
}
