using System;
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

            // Some Emby StateChange reports omit PlaySessionId. They cannot be proven
            // stale and must remain usable for pause/unpause clock state handling.
            Assert.True(registry.IsCurrentPlaybackSession(
                "party",
                "web-session",
                playSessionId: null));
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
