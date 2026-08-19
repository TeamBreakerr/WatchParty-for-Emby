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
        public void PauseRetryStopsAfterTheExpectedEchoIsConsumed()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 18, 16, 0, 0, DateTimeKind.Utc);
            var token = coordinator.ExpectPauseState("ios-session", true, now);

            Assert.True(coordinator.IsPauseStateExpectationPending("ios-session", token, now.AddMilliseconds(300)));
            var classification = coordinator.ClassifyInboundPauseState(
                "ios-session",
                previousIsPaused: false,
                reportedIsPaused: true,
                reportedPositionTicks: TimeSpan.FromSeconds(10).Ticks,
                now.AddSeconds(1));

            Assert.True(classification.IsExpectedCommandEcho);
            Assert.False(coordinator.IsPauseStateExpectationPending("ios-session", token, now.AddSeconds(1)));
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
        public void RepeatedStationaryMasterHeartbeatAfterSeekIsNotAnotherSeek()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 19, 0, 0, 0, DateTimeKind.Utc);

            coordinator.UpdateMasterPosition(
                "party",
                TimeSpan.FromSeconds(685.8).Ticks,
                isPlaying: true,
                now,
                seekThresholdTicks: TimeSpan.FromSeconds(2).Ticks);

            var firstTarget = coordinator.UpdateMasterPositionGuardingReloadArtifact(
                "party",
                TimeSpan.FromSeconds(724.5).Ticks,
                isPlaying: true,
                now.AddSeconds(10),
                seekThresholdTicks: TimeSpan.FromSeconds(2).Ticks,
                nearZeroThresholdTicks: TimeSpan.FromSeconds(5).Ticks,
                priorPositionThresholdTicks: TimeSpan.FromSeconds(30).Ticks);

            var repeatedTarget = coordinator.UpdateMasterPositionGuardingReloadArtifact(
                "party",
                TimeSpan.FromSeconds(724.5).Ticks,
                isPlaying: true,
                now.AddSeconds(20),
                seekThresholdTicks: TimeSpan.FromSeconds(2).Ticks,
                nearZeroThresholdTicks: TimeSpan.FromSeconds(5).Ticks,
                priorPositionThresholdTicks: TimeSpan.FromSeconds(30).Ticks);

            Assert.True(firstTarget.IsSeek);
            Assert.True(repeatedTarget.IsIgnoredStaleHeartbeat);
            Assert.False(repeatedTarget.IsSeek);
            Assert.Equal(
                TimeSpan.FromSeconds(734.5).Ticks,
                coordinator.GetEstimatedPartyPosition(
                    "party",
                    fallbackPositionTicks: 0,
                    now.AddSeconds(20)));
        }

        [Fact]
        public void TransientNearZeroMasterReportPreservesTheAuthoritativeClock()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 17, 15, 27, 0, DateTimeKind.Utc);
            var originalPosition = TimeSpan.FromMinutes(20).Ticks;

            coordinator.UpdateMasterPosition(
                "party",
                originalPosition,
                isPlaying: true,
                now,
                seekThresholdTicks: TimeSpan.FromSeconds(2).Ticks);

            // Production trace from Emby Web: a position-less report is followed by a
            // synthetic ~1s value, then the real continuously-advanced position. The
            // ~1s sample must not replace the authoritative clock.
            var transient = coordinator.UpdateMasterPositionGuardingReloadArtifact(
                "party",
                TimeSpan.FromSeconds(1).Ticks,
                isPlaying: true,
                now.AddSeconds(30),
                seekThresholdTicks: TimeSpan.FromSeconds(2).Ticks,
                nearZeroThresholdTicks: TimeSpan.FromSeconds(5).Ticks,
                priorPositionThresholdTicks: TimeSpan.FromSeconds(30).Ticks);

            Assert.True(transient.IsDeferredReloadArtifact);
            Assert.False(transient.IsSeek);
            Assert.Equal(
                originalPosition + TimeSpan.FromSeconds(30).Ticks,
                transient.AuthoritativePositionTicks);

            var recovered = coordinator.UpdateMasterPositionGuardingReloadArtifact(
                "party",
                originalPosition + TimeSpan.FromSeconds(32).Ticks,
                isPlaying: true,
                now.AddSeconds(32),
                seekThresholdTicks: TimeSpan.FromSeconds(2).Ticks,
                nearZeroThresholdTicks: TimeSpan.FromSeconds(5).Ticks,
                priorPositionThresholdTicks: TimeSpan.FromSeconds(30).Ticks);

            Assert.False(recovered.IsDeferredReloadArtifact);
            Assert.False(recovered.IsSeek);
            Assert.Equal(
                originalPosition + TimeSpan.FromSeconds(33).Ticks,
                coordinator.GetEstimatedPartyPosition(
                    "party",
                    fallbackPositionTicks: 0,
                    now.AddSeconds(33)));
        }

        [Fact]
        public void ImmediateNonZeroReportAfterNearZeroReloadIsIgnored()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 18, 15, 0, 0, DateTimeKind.Utc);

            coordinator.UpdateMasterPosition(
                "party",
                TimeSpan.FromMinutes(20).Ticks,
                isPlaying: true,
                now,
                seekThresholdTicks: TimeSpan.FromSeconds(2).Ticks);

            var staged = coordinator.UpdateMasterPositionGuardingReloadArtifact(
                "party",
                TimeSpan.FromSeconds(1).Ticks,
                isPlaying: true,
                now.AddSeconds(30),
                seekThresholdTicks: TimeSpan.FromSeconds(2).Ticks,
                nearZeroThresholdTicks: TimeSpan.FromSeconds(5).Ticks,
                priorPositionThresholdTicks: TimeSpan.FromSeconds(30).Ticks);
            var stale = coordinator.UpdateMasterPositionGuardingReloadArtifact(
                "party",
                TimeSpan.FromSeconds(20).Ticks,
                isPlaying: true,
                now.AddSeconds(30).AddMilliseconds(80),
                seekThresholdTicks: TimeSpan.FromSeconds(2).Ticks,
                nearZeroThresholdTicks: TimeSpan.FromSeconds(5).Ticks,
                priorPositionThresholdTicks: TimeSpan.FromSeconds(30).Ticks);

            Assert.True(staged.IsDeferredReloadArtifact);
            Assert.True(stale.IsIgnoredReloadArtifactReport);
            Assert.Equal(staged.AuthoritativeRevision, stale.AuthoritativeRevision);
            Assert.Equal(
                TimeSpan.FromMinutes(20).Ticks
                    + (TimeSpan.FromSeconds(30) + TimeSpan.FromMilliseconds(80)).Ticks,
                coordinator.GetEstimatedPartyPosition(
                    "party",
                    fallbackPositionTicks: 0,
                    now.AddSeconds(30).AddMilliseconds(80)));
        }

        [Fact]
        public void MonotonicHeartbeatChainAfterReloadIsIgnoredAsOneStaleGeneration()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 18, 15, 30, 0, DateTimeKind.Utc);
            var originalPosition = TimeSpan.FromMinutes(20).Ticks;

            coordinator.UpdateMasterPosition(
                "party",
                originalPosition,
                isPlaying: true,
                now,
                seekThresholdTicks: TimeSpan.FromSeconds(2).Ticks);
            coordinator.UpdateMasterPositionGuardingReloadArtifact(
                "party",
                TimeSpan.FromSeconds(1).Ticks,
                isPlaying: true,
                now.AddSeconds(30),
                seekThresholdTicks: TimeSpan.FromSeconds(2).Ticks,
                nearZeroThresholdTicks: TimeSpan.FromSeconds(5).Ticks,
                priorPositionThresholdTicks: TimeSpan.FromSeconds(30).Ticks);

            var stale19 = coordinator.UpdateMasterPositionGuardingReloadArtifact(
                "party",
                TimeSpan.FromSeconds(19.924).Ticks,
                isPlaying: true,
                now.AddSeconds(30).AddMilliseconds(80),
                seekThresholdTicks: TimeSpan.FromSeconds(2).Ticks,
                nearZeroThresholdTicks: TimeSpan.FromSeconds(5).Ticks,
                priorPositionThresholdTicks: TimeSpan.FromSeconds(30).Ticks);
            var stale25 = coordinator.UpdateMasterPositionGuardingReloadArtifact(
                "party",
                TimeSpan.FromSeconds(24.924).Ticks,
                isPlaying: true,
                now.AddSeconds(34).AddMilliseconds(900),
                seekThresholdTicks: TimeSpan.FromSeconds(2).Ticks,
                nearZeroThresholdTicks: TimeSpan.FromSeconds(5).Ticks,
                priorPositionThresholdTicks: TimeSpan.FromSeconds(30).Ticks);
            var stale30 = coordinator.UpdateMasterPositionGuardingReloadArtifact(
                "party",
                TimeSpan.FromSeconds(29.924).Ticks,
                isPlaying: true,
                now.AddSeconds(39).AddMilliseconds(900),
                seekThresholdTicks: TimeSpan.FromSeconds(2).Ticks,
                nearZeroThresholdTicks: TimeSpan.FromSeconds(5).Ticks,
                priorPositionThresholdTicks: TimeSpan.FromSeconds(30).Ticks);

            Assert.True(stale19.IsIgnoredReloadArtifactReport);
            Assert.True(stale25.IsIgnoredReloadArtifactReport);
            Assert.True(stale30.IsIgnoredReloadArtifactReport);
            Assert.Equal(
                originalPosition + TimeSpan.FromSeconds(39.9).Ticks,
                coordinator.GetEstimatedPartyPosition(
                    "party",
                    fallbackPositionTicks: 0,
                now.AddSeconds(39).AddMilliseconds(900)));
        }

        [Fact]
        public void ClearlyNewDistantMasterTargetBreaksReloadGenerationImmediately()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 18, 16, 0, 0, DateTimeKind.Utc);

            coordinator.UpdateMasterPosition(
                "party",
                TimeSpan.FromMinutes(20).Ticks,
                isPlaying: true,
                now,
                seekThresholdTicks: TimeSpan.FromSeconds(2).Ticks);
            coordinator.UpdateMasterPositionGuardingReloadArtifact(
                "party",
                TimeSpan.FromSeconds(1).Ticks,
                isPlaying: true,
                now.AddSeconds(30),
                seekThresholdTicks: TimeSpan.FromSeconds(2).Ticks,
                nearZeroThresholdTicks: TimeSpan.FromSeconds(5).Ticks,
                priorPositionThresholdTicks: TimeSpan.FromSeconds(30).Ticks);

            var newTarget = coordinator.UpdateMasterPositionGuardingReloadArtifact(
                "party",
                TimeSpan.FromSeconds(100).Ticks,
                isPlaying: true,
                now.AddSeconds(30).AddMilliseconds(100),
                seekThresholdTicks: TimeSpan.FromSeconds(2).Ticks,
                nearZeroThresholdTicks: TimeSpan.FromSeconds(5).Ticks,
                priorPositionThresholdTicks: TimeSpan.FromSeconds(30).Ticks);

            Assert.True(newTarget.IsSeek);
            Assert.False(newTarget.IsIgnoredReloadArtifactReport);
            Assert.Equal(TimeSpan.FromSeconds(100).Ticks, newTarget.AuthoritativePositionTicks);
        }

        [Fact]
        public void NonMonotonicSmallTailAfterReloadDoesNotReplaceNearZeroCandidate()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 18, 16, 30, 0, DateTimeKind.Utc);

            coordinator.UpdateMasterPosition(
                "party",
                TimeSpan.FromMinutes(20).Ticks,
                isPlaying: true,
                now,
                seekThresholdTicks: TimeSpan.FromSeconds(2).Ticks);
            var staged = coordinator.UpdateMasterPositionGuardingReloadArtifact(
                "party",
                TimeSpan.FromSeconds(1).Ticks,
                isPlaying: true,
                now.AddSeconds(30),
                seekThresholdTicks: TimeSpan.FromSeconds(2).Ticks,
                nearZeroThresholdTicks: TimeSpan.FromSeconds(5).Ticks,
                priorPositionThresholdTicks: TimeSpan.FromSeconds(30).Ticks);
            var stale19 = coordinator.UpdateMasterPositionGuardingReloadArtifact(
                "party",
                TimeSpan.FromSeconds(19.9).Ticks,
                isPlaying: true,
                now.AddSeconds(30).AddMilliseconds(80),
                seekThresholdTicks: TimeSpan.FromSeconds(2).Ticks,
                nearZeroThresholdTicks: TimeSpan.FromSeconds(5).Ticks,
                priorPositionThresholdTicks: TimeSpan.FromSeconds(30).Ticks);
            var stale29 = coordinator.UpdateMasterPositionGuardingReloadArtifact(
                "party",
                TimeSpan.FromSeconds(29.9).Ticks,
                isPlaying: true,
                now.AddSeconds(30).AddMilliseconds(180),
                seekThresholdTicks: TimeSpan.FromSeconds(2).Ticks,
                nearZeroThresholdTicks: TimeSpan.FromSeconds(5).Ticks,
                priorPositionThresholdTicks: TimeSpan.FromSeconds(30).Ticks);

            Assert.True(staged.IsDeferredReloadArtifact);
            Assert.True(stale19.IsIgnoredReloadArtifactReport);
            Assert.True(stale29.IsIgnoredReloadArtifactReport);
            Assert.Equal(
                TimeSpan.FromMinutes(20).Ticks + TimeSpan.FromSeconds(30.18).Ticks,
                coordinator.GetEstimatedPartyPosition(
                    "party",
                    fallbackPositionTicks: 0,
                    now.AddSeconds(30).AddMilliseconds(180)));
        }

        [Fact]
        public void PositionReportFarFromPendingSeekTargetIsIgnoredUntilItSettles()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 18, 15, 0, 0, DateTimeKind.Utc);
            var target = TimeSpan.FromSeconds(2).Ticks;

            Assert.True(coordinator.TryBeginSeek("ios-session", target, now));
            Assert.True(coordinator.ShouldIgnorePositionReport(
                "ios-session",
                TimeSpan.FromSeconds(75).Ticks,
                now.AddSeconds(1),
                out var expectedTarget));
            Assert.Equal(target, expectedTarget);

            Assert.False(coordinator.ShouldIgnorePositionReport(
                "ios-session",
                TimeSpan.FromSeconds(3).Ticks,
                now.AddSeconds(1),
                out expectedTarget));
        }

        [Fact]
        public void ExplicitMasterSeekBecomesAuthoritativeImmediately()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 18, 17, 0, 0, DateTimeKind.Utc);

            coordinator.UpdateMasterPosition(
                "party",
                TimeSpan.FromMinutes(20).Ticks,
                isPlaying: true,
                now,
                seekThresholdTicks: TimeSpan.FromSeconds(2).Ticks);

            var result = coordinator.ApplyExplicitMasterSeek(
                "party",
                TimeSpan.FromSeconds(1).Ticks,
                isPlaying: false,
                now.AddSeconds(1));

            Assert.True(result.IsSeek);
            Assert.Equal(TimeSpan.FromSeconds(1).Ticks, result.AuthoritativePositionTicks);
            Assert.Equal(
                TimeSpan.FromSeconds(1).Ticks,
                coordinator.GetEstimatedPartyPosition(
                    "party",
                    fallbackPositionTicks: 0,
                    now.AddSeconds(10)));
        }

        [Fact]
        public void WebHeartbeatDiscontinuityCanBeIgnoredWithoutChangingTheMasterClock()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 18, 17, 30, 0, DateTimeKind.Utc);
            var originalPosition = TimeSpan.FromMinutes(20).Ticks;

            coordinator.UpdateMasterPosition(
                "party",
                originalPosition,
                isPlaying: false,
                now,
                seekThresholdTicks: TimeSpan.FromSeconds(2).Ticks);

            var result = coordinator.UpdateMasterPositionGuardingReloadArtifact(
                "party",
                TimeSpan.FromSeconds(1).Ticks,
                isPlaying: false,
                now.AddSeconds(1),
                seekThresholdTicks: TimeSpan.FromSeconds(2).Ticks,
                nearZeroThresholdTicks: TimeSpan.FromSeconds(5).Ticks,
                priorPositionThresholdTicks: TimeSpan.FromSeconds(30).Ticks,
                acceptInferredSeek: false);

            Assert.True(result.IsIgnoredUnexpectedDiscontinuity);
            Assert.Equal(originalPosition, result.AuthoritativePositionTicks);
            Assert.Equal(
                originalPosition,
                coordinator.GetEstimatedPartyPosition(
                    "party",
                    fallbackPositionTicks: 0,
                    now.AddSeconds(2)));
        }

        [Fact]
        public void RealSeekNearTheBeginningIsAcceptedWhenPlaybackAdvancesPastTheArtifactBand()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 17, 15, 27, 0, DateTimeKind.Utc);

            coordinator.UpdateMasterPosition(
                "party",
                TimeSpan.FromMinutes(20).Ticks,
                isPlaying: true,
                now,
                seekThresholdTicks: TimeSpan.FromSeconds(2).Ticks);

            var staged = coordinator.UpdateMasterPositionGuardingReloadArtifact(
                "party",
                TimeSpan.FromSeconds(1).Ticks,
                isPlaying: true,
                now.AddSeconds(30),
                seekThresholdTicks: TimeSpan.FromSeconds(2).Ticks,
                nearZeroThresholdTicks: TimeSpan.FromSeconds(5).Ticks,
                priorPositionThresholdTicks: TimeSpan.FromSeconds(30).Ticks);
            var realTarget = coordinator.UpdateMasterPositionGuardingReloadArtifact(
                "party",
                TimeSpan.FromSeconds(7).Ticks,
                isPlaying: true,
                now.AddSeconds(32),
                seekThresholdTicks: TimeSpan.FromSeconds(2).Ticks,
                nearZeroThresholdTicks: TimeSpan.FromSeconds(5).Ticks,
                priorPositionThresholdTicks: TimeSpan.FromSeconds(30).Ticks);

            Assert.True(staged.IsDeferredReloadArtifact);
            Assert.False(realTarget.IsDeferredReloadArtifact);
            Assert.True(realTarget.IsSeek);
            Assert.Equal(TimeSpan.FromSeconds(7).Ticks, realTarget.AuthoritativePositionTicks);
        }

        [Fact]
        public void DeferredNearZeroCommitIsRejectedAfterAuthoritativePositionRecovers()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 18, 0, 0, 0, DateTimeKind.Utc);
            var originalPosition = TimeSpan.FromMinutes(20).Ticks;

            coordinator.UpdateMasterPosition(
                "party",
                originalPosition,
                isPlaying: true,
                now,
                seekThresholdTicks: TimeSpan.FromSeconds(2).Ticks);
            var staged = coordinator.UpdateMasterPositionGuardingReloadArtifact(
                "party",
                TimeSpan.FromSeconds(1).Ticks,
                isPlaying: true,
                now.AddSeconds(30),
                seekThresholdTicks: TimeSpan.FromSeconds(2).Ticks,
                nearZeroThresholdTicks: TimeSpan.FromSeconds(5).Ticks,
                priorPositionThresholdTicks: TimeSpan.FromSeconds(30).Ticks);

            coordinator.UpdateMasterPositionGuardingReloadArtifact(
                "party",
                originalPosition + TimeSpan.FromSeconds(32).Ticks,
                isPlaying: true,
                now.AddSeconds(32),
                seekThresholdTicks: TimeSpan.FromSeconds(2).Ticks,
                nearZeroThresholdTicks: TimeSpan.FromSeconds(5).Ticks,
                priorPositionThresholdTicks: TimeSpan.FromSeconds(30).Ticks);

            Assert.False(coordinator.TryCommitDeferredMasterPosition(
                "party",
                staged.AuthoritativeRevision,
                TimeSpan.FromSeconds(11).Ticks,
                staged.AuthoritativePositionTicks,
                now.AddSeconds(40),
                out _));
            Assert.Equal(
                originalPosition + TimeSpan.FromSeconds(33).Ticks,
                coordinator.GetEstimatedPartyPosition(
                    "party",
                    fallbackPositionTicks: 0,
                    now.AddSeconds(33)));
        }

        [Fact]
        public void DeferredNearZeroCommitSucceedsWithoutANewerAuthoritativeReport()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 18, 0, 0, 0, DateTimeKind.Utc);

            coordinator.UpdateMasterPosition(
                "party",
                TimeSpan.FromMinutes(20).Ticks,
                isPlaying: true,
                now,
                seekThresholdTicks: TimeSpan.FromSeconds(2).Ticks);
            var staged = coordinator.UpdateMasterPositionGuardingReloadArtifact(
                "party",
                TimeSpan.FromSeconds(1).Ticks,
                isPlaying: true,
                now.AddSeconds(30),
                seekThresholdTicks: TimeSpan.FromSeconds(2).Ticks,
                nearZeroThresholdTicks: TimeSpan.FromSeconds(5).Ticks,
                priorPositionThresholdTicks: TimeSpan.FromSeconds(30).Ticks);

            Assert.True(coordinator.TryCommitDeferredMasterPosition(
                "party",
                staged.AuthoritativeRevision,
                TimeSpan.FromSeconds(1).Ticks,
                staged.AuthoritativePositionTicks,
                now.AddSeconds(40),
                out var committedPosition));
            Assert.Equal(TimeSpan.FromSeconds(11).Ticks, committedPosition);
            Assert.Equal(
                TimeSpan.FromSeconds(12).Ticks,
                coordinator.GetEstimatedPartyPosition(
                    "party",
                    fallbackPositionTicks: 0,
                    now.AddSeconds(41)));
        }

        [Fact]
        public void PositionlessPauseDoesNotInvalidateADeferredNearZeroSeek()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 18, 0, 0, 0, DateTimeKind.Utc);

            coordinator.UpdateMasterPosition(
                "party",
                TimeSpan.FromMinutes(20).Ticks,
                isPlaying: true,
                now,
                seekThresholdTicks: TimeSpan.FromSeconds(2).Ticks);
            var staged = coordinator.UpdateMasterPositionGuardingReloadArtifact(
                "party",
                TimeSpan.FromSeconds(1).Ticks,
                isPlaying: true,
                now.AddSeconds(30),
                seekThresholdTicks: TimeSpan.FromSeconds(2).Ticks,
                nearZeroThresholdTicks: TimeSpan.FromSeconds(5).Ticks,
                priorPositionThresholdTicks: TimeSpan.FromSeconds(30).Ticks);

            coordinator.SetMasterPlaybackState(
                "party",
                isPlaying: false,
                fallbackPositionTicks: 0,
                now.AddSeconds(31));

            Assert.True(coordinator.TryCommitDeferredMasterPosition(
                "party",
                staged.AuthoritativeRevision,
                TimeSpan.FromSeconds(1).Ticks,
                staged.AuthoritativePositionTicks,
                now.AddSeconds(40),
                out var committedPosition));
            Assert.Equal(TimeSpan.FromSeconds(2).Ticks, committedPosition);
            Assert.Equal(
                TimeSpan.FromSeconds(2).Ticks,
                coordinator.GetEstimatedPartyPosition(
                    "party",
                    fallbackPositionTicks: 0,
                    now.AddSeconds(41)));
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
        public void NewPlaybackInstanceDoesNotInheritPriorSeekCooldown()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 17, 1, 42, 3, DateTimeKind.Utc);

            Assert.True(coordinator.TryBeginSeek(
                "ios-session",
                TimeSpan.FromSeconds(12).Ticks,
                now));

            // The same Emby SessionId is reused when the controlled client starts the
            // next episode. Its initial sync must not be suppressed for 30 seconds by
            // the previous episode's pending seek.
            Assert.True(coordinator.ResetForNewPlayback(
                "ios-session",
                previousPlaySessionId: "old-playback",
                newPlaySessionId: "new-playback"));
            Assert.True(coordinator.TryBeginSeek(
                "ios-session",
                TimeSpan.FromMinutes(10).Ticks,
                now.AddSeconds(2)));

            // Duplicate PlaybackStart for the same playback must preserve the new seek's
            // cooldown, otherwise repeated starts can create a seek loop.
            Assert.False(coordinator.ResetForNewPlayback(
                "ios-session",
                previousPlaySessionId: "new-playback",
                newPlaySessionId: "new-playback"));
            Assert.False(coordinator.TryBeginSeek(
                "ios-session",
                TimeSpan.FromMinutes(11).Ticks,
                now.AddSeconds(3)));
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
        public void FailedSeekCanBeCancelledAndRetriedImmediately()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 17, 2, 0, 0, DateTimeKind.Utc);
            var target = TimeSpan.FromMinutes(8).Ticks;

            Assert.True(coordinator.TryBeginSeek("ios-session", target, now));
            Assert.True(coordinator.CancelPendingSeek("ios-session", target));
            Assert.True(coordinator.TryBeginSeek(
                "ios-session",
                target,
                now.AddMilliseconds(100)));
        }

        [Fact]
        public void FailedOlderSeekCannotCancelANewerReplacement()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 17, 2, 0, 0, DateTimeKind.Utc);
            var olderTarget = TimeSpan.FromMinutes(8).Ticks;
            var newerTarget = TimeSpan.FromMinutes(12).Ticks;

            Assert.True(coordinator.TryBeginSeek("ios-session", olderTarget, now));
            Assert.True(coordinator.TryBeginSeek(
                "ios-session",
                newerTarget,
                now.AddMilliseconds(50),
                allowReplace: true));

            Assert.False(coordinator.CancelPendingSeek("ios-session", olderTarget));
            Assert.False(coordinator.TryBeginSeek(
                "ios-session",
                newerTarget,
                now.AddMilliseconds(100)));
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
        public void PositionlessPauseDuringSeekWindowIsClassifiedAsReloadEcho()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 17, 10, 0, 0, DateTimeKind.Utc);
            Assert.True(coordinator.TryBeginSeek(
                "ios-session",
                TimeSpan.FromMinutes(10).Ticks,
                now,
                allowReplace: true));

            var positionedPause = coordinator.ClassifyInboundPauseState(
                "ios-session",
                previousIsPaused: false,
                reportedIsPaused: true,
                reportedPositionTicks: TimeSpan.FromMinutes(10).Ticks,
                now.AddSeconds(2));
            var positionlessReloadEcho = coordinator.ClassifyInboundPauseState(
                "ios-session",
                previousIsPaused: false,
                reportedIsPaused: true,
                reportedPositionTicks: null,
                now.AddSeconds(3));

            Assert.True(positionedPause.IsTransition);
            Assert.False(positionedPause.IsSyntheticEcho);
            Assert.True(positionlessReloadEcho.IsSeekCommandEcho);
            Assert.True(positionlessReloadEcho.IsSyntheticEcho);
        }

        [Fact]
        public void StalePositionOppositeStateDuringSeekWindowIsClassifiedAsReloadEcho()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 18, 11, 0, 0, DateTimeKind.Utc);
            var target = TimeSpan.FromSeconds(12).Ticks;

            Assert.True(coordinator.TryBeginSeek(
                "ios-session",
                target,
                now,
                allowReplace: true));

            // iOS can report its old position together with an automatic Unpause while
            // the remote seek rebuilds the stream. It must not become a viewer command.
            var staleState = coordinator.ClassifyInboundPauseState(
                "ios-session",
                previousIsPaused: true,
                reportedIsPaused: false,
                reportedPositionTicks: TimeSpan.FromSeconds(64).Ticks,
                now.AddSeconds(1));

            // A state report at the commanded position remains eligible for real input.
            var targetState = coordinator.ClassifyInboundPauseState(
                "ios-session",
                previousIsPaused: true,
                reportedIsPaused: false,
                reportedPositionTicks: target,
                now.AddSeconds(2));

            Assert.True(staleState.IsSyntheticEcho);
            Assert.True(targetState.IsTransition);
            Assert.False(targetState.IsSyntheticEcho);
        }

        [Fact]
        public void PauseSyncCanForceOneSeekEvenWhenTheSameTargetIsPending()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 16, 11, 30, 0, DateTimeKind.Utc);
            var target = TimeSpan.FromMinutes(10).Ticks;

            Assert.True(coordinator.TryBeginSeek("ios-session", target, now));
            Assert.False(coordinator.TryBeginSeek("ios-session", target, now.AddSeconds(1)));
            Assert.True(coordinator.TryBeginSeek(
                "ios-session",
                target,
                now.AddSeconds(1),
                allowReplace: true,
                force: true));
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

            // An opposite state means the user/client took a different action before
            // the expected echo arrived. Invalidate the old expectation immediately so
            // a later intentional state change is not swallowed.
            Assert.False(coordinator.ConsumeExpectedPauseState(
                "ios-session",
                true,
                now.AddSeconds(4),
                clearOnMismatch: true));
            Assert.False(coordinator.ConsumeExpectedPauseState("ios-session", false, now.AddSeconds(4)));
        }

        [Fact]
        public void SlowIosPauseEchoIsStillConsumedAfterTenSeconds()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 16, 11, 20, 0, DateTimeKind.Utc);

            coordinator.ExpectPauseState("ios-session", true, now);

            // Production trace: the iOS client confirmed a remote Pause after 9.37s
            // while the 115-backed stream was settling. Treat it as the command echo,
            // not a new participant-initiated pause that should control the master.
            Assert.True(coordinator.ConsumeExpectedPauseState(
                "ios-session",
                true,
                now.AddMilliseconds(9370)));
        }

        [Fact]
        public void LatePauseEchoIsNotOverwrittenByANewerUnpauseCommand()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 16, 11, 40, 0, DateTimeKind.Utc);

            coordinator.ExpectPauseState("ios-session", true, now);
            coordinator.ExpectPauseState("ios-session", false, now.AddSeconds(9));

            // Exact production ordering from the regression: the master unpaused while
            // iOS was still settling the prior Pause+Seek. Both delayed command echoes
            // must be consumed in order rather than the newer expectation replacing the
            // older one and turning Paused=true into a participant control action.
            Assert.True(coordinator.ConsumeExpectedPauseState(
                "ios-session",
                true,
                now.AddMilliseconds(9370)));
            Assert.True(coordinator.ConsumeExpectedPauseState(
                "ios-session",
                false,
                now.AddMilliseconds(9500)));
        }

        [Fact]
        public void OutOfOrderUnpauseAndPauseEchoesAreBothConsumed()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 17, 8, 0, 0, DateTimeKind.Utc);

            coordinator.ExpectPauseState("ios-session", true, now);
            coordinator.ExpectPauseState("ios-session", false, now.AddMilliseconds(10));

            Assert.True(coordinator.ConsumeExpectedPauseState(
                "ios-session",
                false,
                now.AddSeconds(1)));
            Assert.True(coordinator.ConsumeExpectedPauseState(
                "ios-session",
                true,
                now.AddSeconds(2)));
        }

        [Fact]
        public void FailedOlderPauseCommandCannotCancelANewerExpectation()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 17, 8, 30, 0, DateTimeKind.Utc);

            var olderPause = coordinator.ExpectPauseState("ios-session", true, now);
            coordinator.ExpectPauseState("ios-session", false, now.AddMilliseconds(10));

            Assert.True(coordinator.CancelExpectedPauseState("ios-session", olderPause));
            Assert.True(coordinator.ConsumeExpectedPauseState(
                "ios-session",
                false,
                now.AddSeconds(1)));
            Assert.False(coordinator.ConsumeExpectedPauseState(
                "ios-session",
                true,
                now.AddSeconds(2)));
        }

        [Fact]
        public void FailedDuplicatePauseCommandCannotCancelTheOtherCommand()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 17, 8, 45, 0, DateTimeKind.Utc);

            var olderPause = coordinator.ExpectPauseState("ios-session", true, now);
            coordinator.ExpectPauseState("ios-session", true, now.AddMilliseconds(10));

            Assert.True(coordinator.CancelExpectedPauseState("ios-session", olderPause));
            Assert.True(coordinator.ConsumeExpectedPauseState(
                "ios-session",
                true,
                now.AddSeconds(1)));
        }

        [Fact]
        public void PausedFirstProgressIsClassifiedBeforeARejoinSeekCreatesEchoes()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 17, 9, 0, 0, DateTimeKind.Utc);

            var inbound = coordinator.ClassifyInboundPauseState(
                "ios-session",
                previousIsPaused: false,
                reportedIsPaused: true,
                now);

            Assert.True(coordinator.TryBeginSeek(
                "ios-session",
                TimeSpan.FromMinutes(5).Ticks,
                now.AddMilliseconds(1)));
            coordinator.ExpectPauseState(
                "ios-session",
                true,
                now.AddMilliseconds(1));

            Assert.True(inbound.IsTransition);
            Assert.False(inbound.IsExpectedCommandEcho);
            Assert.False(inbound.IsSeekCommandEcho);
        }

        [Fact]
        public void OppositeStateAtSeekTargetAfterPauseEchoIsStillSynthetic()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 18, 10, 0, 0, DateTimeKind.Utc);
            var target = TimeSpan.FromSeconds(9.1).Ticks;

            coordinator.ExpectPauseState("ios-session", true, now);
            Assert.True(coordinator.TryBeginSeek("ios-session", target, now));

            var pauseEcho = coordinator.ClassifyInboundPauseState(
                "ios-session",
                previousIsPaused: false,
                reportedIsPaused: true,
                reportedPositionTicks: target,
                now.AddMilliseconds(100));
            Assert.True(pauseEcho.IsExpectedCommandEcho);

            // iOS can immediately report Unpause at the target while rebuilding its
            // player. It is the reverse acknowledgement of the same Pause+Seek, not a
            // viewer command that should resume the master.
            var reverseEcho = coordinator.ClassifyInboundPauseState(
                "ios-session",
                previousIsPaused: true,
                reportedIsPaused: false,
                reportedPositionTicks: target,
                now.AddMilliseconds(250));
            Assert.True(reverseEcho.IsSeekCommandEcho);
            Assert.True(reverseEcho.IsSyntheticEcho);

            var realUnpause = coordinator.ClassifyInboundPauseState(
                "ios-session",
                previousIsPaused: true,
                reportedIsPaused: false,
                reportedPositionTicks: target,
                now.AddSeconds(4));
            Assert.False(realUnpause.IsSyntheticEcho);
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

        [Fact]
        public void PositionlessPauseFreezesAndPositionlessUnpauseResumesTheMasterClock()
        {
            var coordinator = new PlaybackSyncCoordinator();
            var now = new DateTime(2026, 8, 16, 15, 36, 32, DateTimeKind.Utc);
            var reportedPosition = TimeSpan.FromSeconds(3.9).Ticks;

            coordinator.UpdateMasterPosition(
                "party",
                reportedPosition,
                isPlaying: true,
                now,
                TimeSpan.FromSeconds(10).Ticks);

            var frozenPosition = coordinator.SetMasterPlaybackState(
                "party",
                isPlaying: false,
                reportedPosition,
                now.AddSeconds(1));

            Assert.Equal(TimeSpan.FromSeconds(4.9).Ticks, frozenPosition);
            Assert.Equal(
                frozenPosition,
                coordinator.GetEstimatedPartyPosition("party", 0, now.AddSeconds(24)));

            var resumedPosition = coordinator.SetMasterPlaybackState(
                "party",
                isPlaying: true,
                frozenPosition,
                now.AddSeconds(24));
            Assert.Equal(frozenPosition, resumedPosition);
            Assert.Equal(
                frozenPosition + TimeSpan.FromSeconds(2).Ticks,
                coordinator.GetEstimatedPartyPosition("party", 0, now.AddSeconds(26)));
        }

    }
}
