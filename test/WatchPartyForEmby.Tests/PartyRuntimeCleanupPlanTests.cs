using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class PartyRuntimeCleanupPlanTests
    {
        [Fact]
        public void DeletedAndNewlyDeactivatedRoomsBothRequireRuntimeTeardown()
        {
            var plan = PartyRuntimeCleanupPlan.Create(
                new[] { "deleted", "disabled", "still-active" },
                new[] { "deleted", "disabled", "still-active" },
                new[]
                {
                    Party("disabled", isActive: false),
                    Party("still-active", isActive: true),
                    Party("new-disabled", isActive: false)
                });

            Assert.Equal(new[] { "deleted" }, plan.RemovedPartyIds);
            Assert.Equal(new[] { "disabled" }, plan.DeactivatedPartyIds);
            Assert.Contains("deleted", plan.PartyIdsToTeardown);
            Assert.Contains("disabled", plan.PartyIdsToTeardown);
            Assert.DoesNotContain("new-disabled", plan.PartyIdsToTeardown);
        }

        private static WatchPartyItem Party(string id, bool isActive)
        {
            return new WatchPartyItem { Id = id, IsActive = isActive };
        }
    }
}
