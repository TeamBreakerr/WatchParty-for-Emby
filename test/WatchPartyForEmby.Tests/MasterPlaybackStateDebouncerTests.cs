using System;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class MasterPlaybackStateDebouncerTests
    {
        [Fact]
        public void OppositeStateWithinWindowCancelsTransientTransition()
        {
            var debouncer = new MasterPlaybackStateDebouncer(TimeSpan.FromMilliseconds(750));
            var start = new DateTime(2026, 8, 18, 4, 0, 0, DateTimeKind.Utc);

            Assert.True(debouncer.Observe("master", false, true, start).ShouldDefer);
            var cancelled = debouncer.Observe(
                "master",
                stableIsPaused: false,
                reportedIsPaused: false,
                start.AddMilliseconds(500));

            Assert.True(cancelled.ShouldCancel);
            Assert.False(debouncer.TryCommit(
                "master",
                stableIsPaused: false,
                start.AddSeconds(2),
                out _));
        }

        [Fact]
        public void StableTransitionCommitsOnlyAfterDebounceWindow()
        {
            var debouncer = new MasterPlaybackStateDebouncer(TimeSpan.FromMilliseconds(750));
            var start = new DateTime(2026, 8, 18, 4, 0, 0, DateTimeKind.Utc);

            Assert.True(debouncer.Observe("master", false, true, start).ShouldDefer);
            Assert.False(debouncer.TryCommit(
                "master",
                stableIsPaused: false,
                start.AddMilliseconds(749),
                out _));
            Assert.True(debouncer.TryCommit(
                "master",
                stableIsPaused: false,
                start.AddMilliseconds(750),
                out var committed));
            Assert.True(committed);
        }

        [Fact]
        public void ReturningToStableStateDoesNotLeaveAStaleCandidate()
        {
            var debouncer = new MasterPlaybackStateDebouncer(TimeSpan.FromMilliseconds(750));
            var start = new DateTime(2026, 8, 18, 4, 0, 0, DateTimeKind.Utc);

            debouncer.Observe("master", false, true, start);
            debouncer.Observe("master", false, false, start.AddMilliseconds(100));
            Assert.True(debouncer.Observe("master", false, true, start.AddSeconds(1)).ShouldDefer);
            Assert.True(debouncer.TryCommit(
                "master",
                stableIsPaused: false,
                start.AddMilliseconds(1800),
                out var committed));
            Assert.True(committed);
        }
    }
}
