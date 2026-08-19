using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class MasterSeekSourceTrackerTests
    {
        [Fact]
        public void ProgressFallbackRemainsAvailableUntilExplicitSeekForCurrentSession()
        {
            var tracker = new MasterSeekSourceTracker();

            Assert.False(tracker.HasExplicitForSession("party", "web-session"));

            tracker.MarkExplicit("party", "other-session");
            Assert.False(tracker.HasExplicitForSession("party", "web-session"));
            Assert.True(tracker.HasExplicitForSession("party", "other-session"));

            tracker.MarkExplicit("party", "web-session");
            Assert.True(tracker.HasExplicitForSession("party", "web-session"));
        }

        [Fact]
        public void ClearingPartyReEnablesFallbackForARejoinedMaster()
        {
            var tracker = new MasterSeekSourceTracker();
            tracker.MarkExplicit("party", "web-session");

            tracker.ClearParty("party");

            Assert.False(tracker.HasExplicitForSession("party", "web-session"));
        }
    }
}
