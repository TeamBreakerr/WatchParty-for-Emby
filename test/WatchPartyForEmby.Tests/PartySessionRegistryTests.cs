using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class PartySessionRegistryTests
    {
        private static PartyParticipant Participant(
            string userId,
            string sessionId,
            DateTime lastActivityAt,
            string playSessionId = null)
        {
            return new PartyParticipant
            {
                UserId = userId,
                UserName = "user-" + userId,
                SessionId = sessionId,
                PlaySessionId = playSessionId,
                LastActivityAt = lastActivityAt
            };
        }

        [Fact]
        public void SameUserCanHaveMultipleSessionsWithoutOverwriting()
        {
            var registry = new PartySessionRegistry();
            var now = new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);

            registry.AddOrUpdate("party", "ios-session", Participant("user-1", "ios-session", now));
            registry.AddOrUpdate("party", "vidhub-session", Participant("user-1", "vidhub-session", now.AddSeconds(10)));

            Assert.Equal(2, registry.SessionCount("party"));
            Assert.Equal(1, registry.DistinctUserCount("party"));
            Assert.True(registry.HasUser("party", "user-1"));

            Assert.True(registry.TryGetSession("party", "ios-session", out var ios));
            Assert.True(registry.TryGetSession("party", "vidhub-session", out var vidhub));
            Assert.Equal("ios-session", ios.SessionId);
            Assert.Equal("vidhub-session", vidhub.SessionId);
        }

        [Fact]
        public async Task ConcurrentNewUsersCannotExceedTheDistinctUserCapacity()
        {
            var registry = new PartySessionRegistry();
            var now = new DateTime(2026, 8, 17, 3, 0, 0, DateTimeKind.Utc);
            using var startTogether = new Barrier(2);

            Task<bool> Join(string userId)
            {
                return Task.Run(() =>
                {
                    startTogether.SignalAndWait();
                    return registry.TryUpsertSession(
                        "party",
                        "session-" + userId,
                        userId,
                        "User " + userId,
                        "playback-" + userId,
                        now,
                        maxParticipants: 1,
                        out _,
                        out _,
                        out _);
                });
            }

            var results = await Task.WhenAll(Join("a"), Join("b"));

            Assert.Equal(1, results.Count(joined => joined));
            Assert.Equal(1, registry.DistinctUserCount("party"));
            Assert.Equal(1, registry.SessionCount("party"));
        }

        [Fact]
        public void ExistingUserCanAddAndRefreshSessionsWhenThePartyIsAtCapacity()
        {
            var registry = new PartySessionRegistry();
            var now = new DateTime(2026, 8, 17, 3, 15, 0, DateTimeKind.Utc);

            Assert.True(registry.TryUpsertSession(
                "party", "ios-session", "viewer", "Viewer", "playback-1", now,
                maxParticipants: 1,
                out _, out _, out var firstCreated));
            Assert.True(firstCreated);

            Assert.True(registry.TryUpsertSession(
                "party", "web-session", "viewer", "Viewer", "playback-2", now.AddSeconds(1),
                maxParticipants: 1,
                out _, out _, out var secondCreated));
            Assert.True(secondCreated);

            Assert.True(registry.TryUpsertSession(
                "party", "web-session", "viewer", "Viewer", "playback-3", now.AddSeconds(2),
                maxParticipants: 1,
                out var refreshed, out var previousPlayback, out var refreshCreated));
            Assert.False(refreshCreated);
            Assert.Equal("playback-2", previousPlayback);
            Assert.Equal("playback-3", refreshed.PlaySessionId);
            Assert.Equal(1, registry.DistinctUserCount("party"));
            Assert.Equal(2, registry.SessionCount("party"));
        }

        [Fact]
        public void OldSessionStopDoesNotRemoveTheNewSession()
        {
            var registry = new PartySessionRegistry();
            var now = new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);

            registry.AddOrUpdate("party", "old-session", Participant("user-1", "old-session", now));
            registry.AddOrUpdate("party", "new-session", Participant("user-1", "new-session", now.AddSeconds(5)));

            Assert.True(registry.TryRemoveSession("party", "old-session", out var removed, out var wasMaster));
            Assert.Equal("old-session", removed.SessionId);
            Assert.False(wasMaster);

            Assert.True(registry.TryGetSession("party", "new-session", out _));
            Assert.False(registry.TryGetSession("party", "old-session", out _));
            Assert.Equal(1, registry.SessionCount("party"));
            Assert.True(registry.HasUser("party", "user-1"));
        }

        [Fact]
        public void DelayedStopForOldPlaybackDoesNotRemoveReplacementUsingSameSessionId()
        {
            var registry = new PartySessionRegistry();
            var now = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);

            registry.AddOrUpdate(
                "party",
                "ios-session",
                Participant("user-1", "ios-session", now, "old-playback"));
            registry.AddOrUpdate(
                "party",
                "ios-session",
                Participant("user-1", "ios-session", now.AddSeconds(1), "new-playback"));

            Assert.False(registry.TryRemoveSession(
                "party",
                "ios-session",
                "old-playback",
                out _,
                out _));
            Assert.True(registry.TryGetSession("party", "ios-session", out var current));
            Assert.Equal("new-playback", current.PlaySessionId);

            Assert.True(registry.TryRemoveSession(
                "party",
                "ios-session",
                "new-playback",
                out _,
                out _));
        }

        [Fact]
        public void StopWithoutPlaybackIdDoesNotRemoveAKnownCurrentPlayback()
        {
            var registry = new PartySessionRegistry();
            var now = new DateTime(2026, 8, 16, 12, 30, 0, DateTimeKind.Utc);
            registry.AddOrUpdate(
                "party",
                "ios-session",
                Participant("user-1", "ios-session", now, "current-playback"));

            Assert.False(registry.TryRemoveSession(
                "party",
                "ios-session",
                expectedPlaySessionId: null,
                out _,
                out _));
            Assert.True(registry.TryGetSession("party", "ios-session", out _));
        }

        [Fact]
        public void ExplicitUnconditionalRemovalRemovesAKnownPlayback()
        {
            var registry = new PartySessionRegistry();
            var now = new DateTime(2026, 8, 16, 12, 45, 0, DateTimeKind.Utc);
            registry.AddOrUpdate(
                "party",
                "ios-session",
                Participant("user-1", "ios-session", now, "current-playback"));

            Assert.True(registry.TryRemoveSession(
                "party",
                "ios-session",
                out var removed,
                out _));
            Assert.Equal("current-playback", removed.PlaySessionId);
            Assert.False(registry.TryGetSession("party", "ios-session", out _));
        }

        [Fact]
        public void InactiveCleanupCannotRemoveASessionThatBecameActiveAfterSnapshot()
        {
            var registry = new PartySessionRegistry();
            var threshold = new DateTime(2026, 8, 16, 13, 0, 0, DateTimeKind.Utc);
            registry.AddOrUpdate(
                "party",
                "ios-session",
                Participant(
                    "user-1",
                    "ios-session",
                    threshold.AddMinutes(-1),
                    "current-playback"));

            Assert.True(registry.UpdateActivity(
                "party",
                "ios-session",
                positionTicks: 10,
                isPaused: false,
                threshold.AddSeconds(1)));

            Assert.False(registry.TryRemoveSessionIfInactive(
                "party",
                "ios-session",
                threshold,
                out _,
                out _));
            Assert.True(registry.TryGetSession("party", "ios-session", out _));
        }

        [Fact]
        public void InactiveCleanupAtomicallyRemovesAStillInactiveSession()
        {
            var registry = new PartySessionRegistry();
            var threshold = new DateTime(2026, 8, 16, 13, 30, 0, DateTimeKind.Utc);
            registry.AddOrUpdate(
                "party",
                "ios-session",
                Participant(
                    "user-1",
                    "ios-session",
                    threshold.AddMinutes(-1),
                    "current-playback"));

            Assert.True(registry.TryRemoveSessionIfInactive(
                "party",
                "ios-session",
                threshold,
                out var removed,
                out _));
            Assert.Equal("ios-session", removed.SessionId);
            Assert.False(registry.TryGetSession("party", "ios-session", out _));
        }

        [Fact]
        public void ProgressFromOldPlaybackIsRejectedAfterNewPlaybackBecomesCurrent()
        {
            var registry = new PartySessionRegistry();
            var now = new DateTime(2026, 8, 17, 0, 35, 0, DateTimeKind.Utc);
            registry.AddOrUpdate(
                "party",
                "web-session",
                Participant("master", "web-session", now, "new-playback"));

            Assert.True(registry.IsCurrentPlaybackSession(
                "party",
                "web-session",
                "new-playback"));
            Assert.False(registry.IsCurrentPlaybackSession(
                "party",
                "web-session",
                "old-playback"));

            // Once a playback generation is known, an event without an identity cannot
            // be attributed to that generation and must not affect authoritative state.
            Assert.False(registry.IsCurrentPlaybackSession(
                "party",
                "web-session",
                playSessionId: null));
        }

        [Fact]
        public void RepeatedUnknownProgressNeverReplacesAKnownPlayback()
        {
            var registry = new PartySessionRegistry();
            var now = new DateTime(2026, 8, 17, 0, 45, 0, DateTimeKind.Utc);
            registry.AddOrUpdate(
                "party",
                "web-session",
                Participant("master", "web-session", now, "old-playback"));

            // One unknown report is not trustworthy: it may be a delayed callback from
            // another Web playback instance sharing this SessionId.
            Assert.False(registry.IsCurrentPlaybackSession(
                "party",
                "web-session",
                "new-playback"));
            Assert.True(registry.IsCurrentPlaybackSession(
                "party",
                "web-session",
                "old-playback"));

            // A second delayed callback is still not proof of a replacement playback.
            Assert.False(registry.IsCurrentPlaybackSession(
                "party",
                "web-session",
                "new-playback"));
            Assert.True(registry.IsCurrentPlaybackSession(
                "party",
                "web-session",
                "old-playback"));
        }

        [Fact]
        public void QualityChangeCanAdoptAReplacementWithoutPlaybackStart()
        {
            var registry = new PartySessionRegistry();
            var now = new DateTime(2026, 8, 24, 13, 2, 47, DateTimeKind.Utc);
            registry.UpsertSession(
                "party",
                "web-session",
                "master",
                "Master",
                "old-playback",
                now.AddSeconds(-2),
                out _,
                out _);

            Assert.True(registry.TryAdoptQualityChangePlayback(
                "party",
                "web-session",
                "reloaded-playback",
                now,
                out var previousPlayback));
            Assert.Equal("old-playback", previousPlayback);
            Assert.True(registry.IsCurrentPlaybackSession(
                "party",
                "web-session",
                "reloaded-playback"));
            Assert.False(registry.IsCurrentPlaybackSession(
                "party",
                "web-session",
                "old-playback"));
            Assert.True(registry.IsRetiredPlaybackId(
                "party",
                "web-session",
                "old-playback"));
        }

        [Fact]
        public void QualityChangeCannotAdoptAPlaybackThatWasAlreadyRetired()
        {
            var registry = new PartySessionRegistry();
            var now = new DateTime(2026, 8, 24, 13, 3, 0, DateTimeKind.Utc);
            registry.UpsertSession(
                "party",
                "web-session",
                "master",
                "Master",
                "old-playback",
                now,
                out _,
                out _);
            registry.UpsertSession(
                "party",
                "web-session",
                "master",
                "Master",
                "current-playback",
                now.AddSeconds(1),
                out _,
                out _);

            Assert.False(registry.TryAdoptQualityChangePlayback(
                "party",
                "web-session",
                "old-playback",
                now.AddSeconds(2),
                out _));
            Assert.True(registry.IsCurrentPlaybackSession(
                "party",
                "web-session",
                "current-playback"));
        }

        [Fact]
        public void UnknownProgressCannotReplaceAKnownPlaybackRegardlessOfLastActivity()
        {
            var registry = new PartySessionRegistry();
            var now = new DateTime(2026, 8, 17, 0, 45, 30, DateTimeKind.Utc);
            registry.AddOrUpdate(
                "party",
                "web-session",
                Participant("master", "web-session", now.AddHours(-1), "old-playback"));

            Assert.False(registry.IsCurrentPlaybackSession(
                "party",
                "web-session",
                "new-playback"));
            Assert.True(registry.IsCurrentPlaybackSession(
                "party",
                "web-session",
                "old-playback"));
        }

        [Fact]
        public void RetiredPlaybackCannotChangeTheCurrentGenerationSnapshot()
        {
            var registry = new PartySessionRegistry();
            var now = new DateTime(2026, 8, 22, 1, 0, 0, DateTimeKind.Utc);
            registry.UpsertSession(
                "party", "master-session", "master", "Master", "old-playback", now,
                out _, out _);
            registry.UpsertSession(
                "party", "master-session", "master", "Master", "current-playback", now.AddSeconds(1),
                out _, out _);
            Assert.True(registry.UpdateActivity(
                "party",
                "master-session",
                "current-playback",
                TimeSpan.FromSeconds(33).Ticks,
                isPaused: true,
                now.AddSeconds(2)));

            Assert.False(registry.IsCurrentPlaybackSession(
                "party",
                "master-session",
                "old-playback"));
            Assert.False(registry.UpdateActivity(
                "party",
                "master-session",
                "old-playback",
                TimeSpan.FromSeconds(774).Ticks,
                isPaused: false,
                nowUtc: now.AddSeconds(10)));
            Assert.True(registry.TryGetSession("party", "master-session", out var current));
            Assert.Equal("current-playback", current.PlaySessionId);
            Assert.Equal(TimeSpan.FromSeconds(33).Ticks, current.CurrentPositionTicks);
            Assert.True(current.IsPaused);
        }

        [Fact]
        public void ProgressCannotEstablishIdentityWhenPlaybackStartWasMissing()
        {
            var registry = new PartySessionRegistry();
            var now = new DateTime(2026, 8, 17, 0, 46, 0, DateTimeKind.Utc);
            registry.AddOrUpdate(
                "party",
                "web-session",
                Participant("master", "web-session", now, playSessionId: null));

            Assert.False(registry.IsCurrentPlaybackSession(
                "party",
                "web-session",
                "new-playback"));
            Assert.True(registry.TryGetSession("party", "web-session", out var current));
            Assert.Null(current.PlaySessionId);
        }

        [Fact]
        public void InterleavedLatePlaybackCannotTakeOverTheCurrentInstance()
        {
            var registry = new PartySessionRegistry();
            var now = new DateTime(2026, 8, 17, 0, 47, 0, DateTimeKind.Utc);
            registry.AddOrUpdate(
                "party",
                "web-session",
                Participant("master", "web-session", now, "current-playback"));

            Assert.False(registry.IsCurrentPlaybackSession(
                "party",
                "web-session",
                "late-playback"));

            Assert.True(registry.IsCurrentPlaybackSession(
                "party",
                "web-session",
                "current-playback"));

            Assert.False(registry.IsCurrentPlaybackSession(
                "party",
                "web-session",
                "late-playback"));
            Assert.True(registry.IsCurrentPlaybackSession(
                "party",
                "web-session",
                "current-playback"));
        }

        [Fact]
        public void PlaybackStartRetiresPreviousIdSoDelayedProgressCannotReclaimSession()
        {
            var registry = new PartySessionRegistry();
            var now = new DateTime(2026, 8, 17, 0, 50, 0, DateTimeKind.Utc);
            registry.UpsertSession(
                "party",
                "web-session",
                "master",
                "Master",
                "old-playback",
                now,
                out _,
                out _);
            registry.UpsertSession(
                "party",
                "web-session",
                "master",
                "Master",
                "new-playback",
                now.AddSeconds(1),
                out _,
                out _);

            Assert.False(registry.IsCurrentPlaybackSession(
                "party",
                "web-session",
                "old-playback"));
            Assert.True(registry.IsCurrentPlaybackSession(
                "party",
                "web-session",
                "new-playback"));
        }

        [Fact]
        public void DelayedProgressCannotReclaimARecreatedSessionAfterItsPlaybackStopped()
        {
            var registry = new PartySessionRegistry();
            var now = new DateTime(2026, 8, 17, 2, 30, 0, DateTimeKind.Utc);
            registry.UpsertSession(
                "party",
                "ios-session",
                "viewer",
                "Viewer",
                "playback-1",
                now,
                out _,
                out _);

            Assert.True(registry.TryRemoveSession(
                "party",
                "ios-session",
                "playback-1",
                out _,
                out _));

            registry.UpsertSession(
                "party",
                "ios-session",
                "viewer",
                "Viewer",
                "playback-2",
                now.AddSeconds(1),
                out _,
                out _);

            Assert.False(registry.IsCurrentPlaybackSession(
                "party",
                "ios-session",
                "playback-1"));
            Assert.True(registry.TryGetSession("party", "ios-session", out var current));
            Assert.Equal("playback-2", current.PlaySessionId);
        }

        [Fact]
        public void RetiredMasterProgressCannotRecreateMembershipAfterCurrentPlaybackStops()
        {
            var registry = new PartySessionRegistry();
            var now = new DateTime(2026, 8, 19, 14, 46, 0, DateTimeKind.Utc);

            registry.UpsertSession(
                "party", "web-session", "master", "Master", "episode-5-playback", now,
                out _, out _);
            registry.UpsertSession(
                "party", "web-session", "master", "Master", "episode-6-playback", now.AddMinutes(1),
                out _, out _);
            registry.UpsertSession(
                "party", "web-session", "master", "Master", "episode-7-playback", now.AddMinutes(2),
                out _, out _);
            Assert.True(registry.SetMasterSession("party", "web-session"));

            Assert.True(registry.TryRemoveSession(
                "party",
                "web-session",
                "episode-7-playback",
                out _,
                out var removedMaster));
            Assert.True(removedMaster);

            Assert.False(registry.IsCurrentPlaybackSession(
                "party",
                "web-session",
                "episode-5-playback"));
            Assert.False(registry.IsCurrentPlaybackSession(
                "party",
                "web-session",
                "episode-6-playback"));
            Assert.False(registry.IsCurrentPlaybackSession(
                "party",
                "web-session",
                "zombie-created-after-stop"));
            Assert.Equal(0, registry.SessionCount("party"));
            Assert.Null(registry.GetMasterSession("party"));
        }

        [Fact]
        public void ProgressCannotCreateMembershipWithoutPlaybackStart()
        {
            var registry = new PartySessionRegistry();

            Assert.False(registry.IsCurrentPlaybackSession(
                "party",
                "web-session",
                "fresh-but-unconfirmed-playback"));
            Assert.Equal(0, registry.SessionCount("party"));
        }

        [Fact]
        public void RetiredPlaybackHistoryKeepsRecentIdsAndEvictsTheOldestAtItsConfiguredLimit()
        {
            var registry = new PartySessionRegistry(maxRetiredPlaybackIdsPerParty: 2);
            var now = new DateTime(2026, 8, 17, 2, 45, 0, DateTimeKind.Utc);

            registry.UpsertSession(
                "party", "session-1", "viewer-1", "Viewer 1", "playback-1", now,
                out _, out _);
            Assert.True(registry.TryRemoveSession(
                "party", "session-1", "playback-1", out _, out _));
            registry.UpsertSession(
                "party", "session-2", "viewer-2", "Viewer 2", "playback-2", now.AddSeconds(1),
                out _, out _);
            Assert.True(registry.TryRemoveSession(
                "party", "session-2", "playback-2", out _, out _));
            registry.UpsertSession(
                "party", "session-3", "viewer-3", "Viewer 3", "playback-3", now.AddSeconds(2),
                out _, out _);
            Assert.True(registry.TryRemoveSession(
                "party", "session-3", "playback-3", out _, out _));

            Assert.True(registry.IsRetiredPlaybackId(
                "party", "session-2", "playback-2"));
            Assert.False(registry.IsRetiredPlaybackId(
                "party", "session-1", "playback-1"));
        }

        [Fact]
        public void DelayedStartForRetiredPlaybackIsRecognizedAsStale()
        {
            var registry = new PartySessionRegistry();
            var now = new DateTime(2026, 8, 17, 3, 0, 0, DateTimeKind.Utc);

            registry.UpsertSession(
                "party", "web-session", "master", "Master", "old-playback", now,
                out _, out _);
            registry.UpsertSession(
                "party", "web-session", "master", "Master", "new-playback", now.AddSeconds(1),
                out _, out _);

            Assert.True(registry.IsRetiredPlaybackId(
                "party", "web-session", "old-playback"));
            Assert.False(registry.IsRetiredPlaybackId(
                "party", "web-session", "new-playback"));
            Assert.False(registry.IsRetiredPlaybackId(
                "party", "web-session", "unknown-playback"));
        }

        [Fact]
        public void ClearPartyClearsPlaybackTombstonesBeforeThePartyIsRecreated()
        {
            var registry = new PartySessionRegistry();
            var now = new DateTime(2026, 8, 17, 2, 50, 0, DateTimeKind.Utc);
            registry.UpsertSession(
                "party", "ios-session", "viewer", "Viewer", "playback-1", now,
                out _, out _);
            Assert.True(registry.TryRemoveSession(
                "party", "ios-session", "playback-1", out _, out _));

            registry.ClearParty("party");
            Assert.False(registry.IsRetiredPlaybackId(
                "party", "ios-session", "playback-1"));
            Assert.False(registry.IsCurrentPlaybackSession(
                "party", "ios-session", "playback-1"));
        }

        [Fact]
        public void ReturnedParticipantIsASnapshotAndCannotMutateRegistryState()
        {
            var registry = new PartySessionRegistry();
            var now = new DateTime(2026, 8, 17, 1, 0, 0, DateTimeKind.Utc);
            registry.AddOrUpdate(
                "party",
                "ios-session",
                Participant("user-1", "ios-session", now, "current-playback"));

            Assert.True(registry.TryGetSession("party", "ios-session", out var snapshot));
            snapshot.PlaySessionId = "stale-playback";
            snapshot.CurrentPositionTicks = TimeSpan.FromHours(1).Ticks;

            Assert.True(registry.TryGetSession("party", "ios-session", out var current));
            Assert.Equal("current-playback", current.PlaySessionId);
            Assert.Equal(0, current.CurrentPositionTicks);
        }

        [Fact]
        public void SessionIdentityAndActivityAreUpdatedThroughAtomicOperations()
        {
            var registry = new PartySessionRegistry();
            var startedAt = new DateTime(2026, 8, 17, 1, 30, 0, DateTimeKind.Utc);
            var progressedAt = startedAt.AddSeconds(10);

            var participant = registry.UpsertSession(
                "party",
                "web-session",
                "master-user",
                "Master",
                "playback-1",
                startedAt,
                out var previousPlayback,
                out var created);

            Assert.True(created);
            Assert.Null(previousPlayback);
            Assert.Equal("playback-1", participant.PlaySessionId);
            Assert.True(registry.UpdateActivity(
                "party",
                "web-session",
                TimeSpan.FromMinutes(12).Ticks,
                isPaused: true,
                progressedAt));

            Assert.True(registry.TryGetSession("party", "web-session", out var current));
            Assert.Equal(TimeSpan.FromMinutes(12).Ticks, current.CurrentPositionTicks);
            Assert.True(current.IsPaused);
            Assert.Equal(progressedAt, current.LastActivityAt);
            Assert.True(registry.SetBuffering("party", "web-session", isBuffering: true));
            Assert.True(registry.TryGetSession("party", "web-session", out current));
            Assert.True(current.IsBuffering);

            registry.UpsertSession(
                "party",
                "web-session",
                "master-user",
                "Renamed Master",
                "playback-2",
                progressedAt.AddSeconds(1),
                out previousPlayback,
                out created);

            Assert.False(created);
            Assert.Equal("playback-1", previousPlayback);
            Assert.True(registry.TryGetSession("party", "web-session", out current));
            Assert.Equal("playback-2", current.PlaySessionId);
            Assert.Equal("Renamed Master", current.UserName);
            Assert.Equal(TimeSpan.FromMinutes(12).Ticks, current.CurrentPositionTicks);
        }

        [Fact]
        public void EpisodeResetClearsRuntimeValuesButPreservesMembershipAndPlaybackIdentity()
        {
            var registry = new PartySessionRegistry();
            var now = new DateTime(2026, 8, 17, 2, 0, 0, DateTimeKind.Utc);
            registry.AddOrUpdate(
                "party",
                "ios-session",
                new PartyParticipant
                {
                    UserId = "user-1",
                    SessionId = "ios-session",
                    PlaySessionId = "playback-1",
                    CurrentPositionTicks = TimeSpan.FromMinutes(42).Ticks,
                    IsPaused = true,
                    IsBuffering = true,
                    LastActivityAt = now
                });

            Assert.True(registry.ResetEpisodeState("party"));

            Assert.True(registry.TryGetSession("party", "ios-session", out var current));
            Assert.Equal("user-1", current.UserId);
            Assert.Equal("playback-1", current.PlaySessionId);
            Assert.Equal(0, current.CurrentPositionTicks);
            Assert.False(current.IsPaused);
            Assert.False(current.IsBuffering);
            Assert.False(registry.ResetEpisodeState("missing-party"));
        }

        [Fact]
        public void SecondMasterUserSessionDoesNotOverwriteTheRegisteredMaster()
        {
            var registry = new PartySessionRegistry();
            var now = new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);

            registry.AddOrUpdate("party", "master-ios", Participant("master", "master-ios", now));
            registry.AddOrUpdate("party", "master-vidhub", Participant("master", "master-vidhub", now.AddSeconds(5)));

            Assert.True(registry.SetMasterSessionIfAbsent("party", "master-ios"));
            Assert.False(registry.SetMasterSessionIfAbsent("party", "master-vidhub"));

            Assert.True(registry.IsMasterSession("party", "master-ios"));
            Assert.False(registry.IsMasterSession("party", "master-vidhub"));
            Assert.Equal("master-ios", registry.GetMasterSession("party"));
        }

        [Fact]
        public void MasterPromotionUsesTheMostRecentRemainingSessionOfTheSameUser()
        {
            var registry = new PartySessionRegistry();
            var now = new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);

            registry.AddOrUpdate("party", "master-ios", Participant("master", "master-ios", now));
            registry.AddOrUpdate("party", "master-vidhub", Participant("master", "master-vidhub", now.AddSeconds(5)));
            Assert.True(registry.SetMasterSession("party", "master-ios"));

            Assert.True(registry.TryRemoveSession("party", "master-ios", out _, out var wasMaster));
            Assert.True(wasMaster);
            Assert.False(registry.IsMasterSession("party", "master-ios"));

            Assert.True(registry.PromoteLatestSessionForUser("party", "master", out var promoted));
            Assert.Equal("master-vidhub", promoted);
            Assert.True(registry.IsMasterSession("party", "master-vidhub"));
        }

        [Fact]
        public void RemovingTheLastSessionOfAUserClearsTheirPresence()
        {
            var registry = new PartySessionRegistry();
            var now = new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);

            registry.AddOrUpdate("party", "session-1", Participant("user-1", "session-1", now));
            registry.AddOrUpdate("party", "session-2", Participant("user-2", "session-2", now));

            Assert.True(registry.TryRemoveSession("party", "session-1", out _, out _));
            Assert.False(registry.HasUser("party", "user-1"));
            Assert.True(registry.HasUser("party", "user-2"));
            Assert.Equal(1, registry.DistinctUserCount("party"));

            Assert.True(registry.TryRemoveSession("party", "session-2", out _, out _));
            Assert.Equal(0, registry.SessionCount("party"));
            Assert.Equal(0, registry.DistinctUserCount("party"));
        }

        [Fact]
        public void ClearPartyRemovesSessionsAndMaster()
        {
            var registry = new PartySessionRegistry();
            var now = new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);

            registry.AddOrUpdate("party", "master-ios", Participant("master", "master-ios", now));
            registry.SetMasterSession("party", "master-ios");

            registry.ClearParty("party");

            Assert.Equal(0, registry.SessionCount("party"));
            Assert.Null(registry.GetMasterSession("party"));
            Assert.False(registry.HasUser("party", "master"));
        }
    }
}
