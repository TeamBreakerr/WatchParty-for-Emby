using System;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class PlaybackSyncCoordinatorTests
    {
        [Fact]
        public void SameTargetSeeksAreSuppressedUntilConfirmationOrExpiry()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);

            Assert.True(coordinator.TryBeginSeek("mac-session", TimeSpan.FromMinutes(10).Ticks, now));
            Assert.False(coordinator.TryBeginSeek("mac-session", TimeSpan.FromMinutes(10).Ticks, now));
            Assert.True(coordinator.IsSeekSettling("mac-session", now.AddSeconds(7)));

            Assert.True(coordinator.ConfirmSeekTarget(
                "mac-session",
                TimeSpan.FromMinutes(10).Ticks + TimeSpan.FromSeconds(1).Ticks,
                now.AddSeconds(7)));
            Assert.True(coordinator.IsSeekSettling("mac-session", now.AddSeconds(7)));

            Assert.False(coordinator.TryBeginSeek("mac-session", TimeSpan.FromMinutes(10).Ticks, now.AddSeconds(7)));
            Assert.True(coordinator.TryBeginSeek("mac-session", TimeSpan.FromMinutes(10).Ticks, now.AddSeconds(31)));
        }

        [Fact]
        public void ContinuousDragReplacesThePendingSeekTarget()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);

            Assert.True(coordinator.TryBeginSeek("mac-session", TimeSpan.FromMinutes(10).Ticks, now));
            Assert.True(coordinator.TryBeginSeek("mac-session", TimeSpan.FromMinutes(12).Ticks, now.AddSeconds(2), allowReplace: true));
            Assert.True(coordinator.IsSeekSettling("mac-session", now.AddSeconds(2)));

            Assert.False(coordinator.TryBeginSeek("mac-session", TimeSpan.FromMinutes(12).Ticks, now.AddSeconds(3)));
            Assert.True(coordinator.ConfirmSeekTarget(
                "mac-session",
                TimeSpan.FromMinutes(12).Ticks,
                now.AddSeconds(3)));
            Assert.False(coordinator.TryBeginSeek("mac-session", TimeSpan.FromMinutes(12).Ticks, now.AddSeconds(3)));
        }

        [Fact]
        public void PeriodicCorrectionCannotReplacePendingMasterSeek()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);

            // Master dragged to 10:00; the seek is still settling.
            Assert.True(coordinator.TryBeginSeek(
                "ios-session",
                TimeSpan.FromMinutes(10).Ticks,
                now,
                allowReplace: true));

            // A periodic correction must not stomp the pending master seek with a
            // slightly-different target; otherwise the client gets bounced around.
            Assert.False(coordinator.TryBeginSeek(
                "ios-session",
                TimeSpan.FromMinutes(10).Ticks + TimeSpan.FromSeconds(3).Ticks,
                now.AddSeconds(2)));

            // The master keeps dragging: a new master seek may replace the pending target.
            Assert.True(coordinator.TryBeginSeek(
                "ios-session",
                TimeSpan.FromMinutes(12).Ticks,
                now.AddSeconds(3),
                allowReplace: true));
        }

        [Fact]
        public void ConfirmedSeekKeepsTheQuietPeriodButAllowsANewMasterSeek()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);

            Assert.True(coordinator.TryBeginSeek(
                "ios-session",
                TimeSpan.FromMinutes(10).Ticks,
                now,
                allowReplace: true));
            Assert.True(coordinator.ConfirmSeekTarget(
                "ios-session",
                TimeSpan.FromMinutes(10).Ticks,
                now.AddSeconds(1)));

            // A transient client echo must not let periodic calibration command again.
            Assert.False(coordinator.TryBeginSeek(
                "ios-session",
                TimeSpan.FromMinutes(9).Ticks + TimeSpan.FromSeconds(50).Ticks,
                now.AddSeconds(10)));

            // A deliberate newer seek from the master can still replace the target.
            Assert.True(coordinator.TryBeginSeek(
                "ios-session",
                TimeSpan.FromMinutes(12).Ticks,
                now.AddSeconds(10),
                allowReplace: true));
        }

        [Fact]
        public void ExpiredPendingSeekAllowsARetry()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);

            Assert.True(coordinator.TryBeginSeek("mac-session", TimeSpan.FromMinutes(10).Ticks, now));
            // The quiet period after a commanded seek is 30s: slow sources like
            // 115-backed streams need that long to restart, so re-commanding earlier
            // only restarts the buffering loop.
            Assert.True(coordinator.IsSeekSettling("mac-session", now.AddSeconds(29)));
            Assert.False(coordinator.IsSeekSettling("mac-session", now.AddSeconds(31)));
            Assert.True(coordinator.TryBeginSeek("mac-session", TimeSpan.FromMinutes(10).Ticks, now.AddSeconds(31)));
        }

        [Fact]
        public void SeekIsNotConfirmedWhileTheClientIsFarFromTheTarget()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);

            Assert.True(coordinator.TryBeginSeek("ios-session", TimeSpan.FromMinutes(20).Ticks, now));
            Assert.False(coordinator.ConfirmSeekTarget(
                "ios-session",
                TimeSpan.FromMinutes(5).Ticks,
                now.AddSeconds(2)));
            Assert.True(coordinator.IsSeekSettling("ios-session", now.AddSeconds(2)));
        }

        [Fact]
        public void TransientTargetEchoDoesNotEndTheSeekQuietPeriod()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 16, 10, 16, 26, DateTimeKind.Utc);
            var target = TimeSpan.FromSeconds(23).Ticks;

            Assert.True(coordinator.TryBeginSeek(
                "ios-session",
                target,
                now,
                allowReplace: true));

            // Emby iOS briefly moves its UI to the remote target, reports that value,
            // then its native player reports the old position again. A single target
            // echo is useful confirmation, but must not remove the 30-second quiet
            // period or periodic calibration will issue another seek every 5 seconds.
            Assert.True(coordinator.ConfirmSeekTarget(
                "ios-session",
                target,
                now.AddSeconds(1)));
            Assert.True(coordinator.IsSeekSettling("ios-session", now.AddSeconds(2)));
            Assert.False(coordinator.TryBeginSeek(
                "ios-session",
                target + TimeSpan.FromSeconds(5).Ticks,
                now.AddSeconds(5)));
        }

        [Fact]
        public void SeekStateEchoWindowDoesNotSuppressPauseForTheWholeBufferCooldown()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 16, 11, 0, 0, DateTimeKind.Utc);

            Assert.True(coordinator.TryBeginSeek(
                "ios-session",
                TimeSpan.FromMinutes(10).Ticks,
                now,
                allowReplace: true));

            Assert.True(coordinator.IsSeekStateEchoExpected("ios-session", now.AddSeconds(7)));
            Assert.False(coordinator.IsSeekStateEchoExpected("ios-session", now.AddSeconds(9)));

            // Buffer protection remains active even after pause/unpause changes should
            // once again be treated as intentional user input.
            Assert.True(coordinator.IsSeekSettling("ios-session", now.AddSeconds(9)));
        }

        [Fact]
        public void PauseAndUnpauseEchoesAreConsumedInsteadOfBeingRebroadcast()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);

            coordinator.ExpectPauseState("ios-session", true, now);

            Assert.True(coordinator.ConsumeExpectedPauseState("ios-session", true, now.AddSeconds(1)));
            Assert.False(coordinator.ConsumeExpectedPauseState("ios-session", true, now.AddSeconds(2)));

            coordinator.ExpectPauseState("ios-session", false, now.AddSeconds(3));

            Assert.False(coordinator.ConsumeExpectedPauseState("ios-session", true, now.AddSeconds(4)));
            Assert.True(coordinator.ConsumeExpectedPauseState("ios-session", false, now.AddSeconds(4)));
        }

        [Fact]
        public void MasterClockProjectsPositionBetweenSparseClientReports()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);
            var start = TimeSpan.FromMinutes(10).Ticks;

            Assert.False(coordinator.UpdateMasterPosition(
                "party", start, true, now, TimeSpan.FromSeconds(10).Ticks));

            Assert.Equal(
                start + TimeSpan.FromSeconds(5).Ticks,
                coordinator.GetEstimatedPartyPosition("party", 0, now.AddSeconds(5)));

            Assert.False(coordinator.UpdateMasterPosition(
                "party", start + TimeSpan.FromSeconds(6).Ticks, true, now.AddSeconds(6), TimeSpan.FromSeconds(10).Ticks));

            Assert.True(coordinator.UpdateMasterPosition(
                "party", TimeSpan.FromMinutes(20).Ticks, true, now.AddSeconds(7), TimeSpan.FromSeconds(10).Ticks));
            Assert.Equal(
                TimeSpan.FromMinutes(20).Ticks,
                coordinator.GetEstimatedPartyPosition("party", 0, now.AddSeconds(7)));
        }

    }
}
