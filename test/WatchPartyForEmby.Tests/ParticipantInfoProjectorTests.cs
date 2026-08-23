using System;
using WatchPartyForEmby.Api;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class ParticipantInfoProjectorTests
    {
        [Fact]
        public void SameUserSessionsRemainSeparateAndOnlyRegisteredMasterIsHost()
        {
            var participants = new[]
            {
                Participant("same-user", "vidhub-session", "VidHub", 20),
                Participant("same-user", "ios-session", "iPhone", 10)
            };
            var sessions = new[]
            {
                new ParticipantSessionDescriptor
                {
                    SessionId = "vidhub-session",
                    Client = "VidHub",
                    SupportsRemoteControl = false,
                    IsOnline = true,
                    HasActiveWebSocket = false,
                    IsDormant = false,
                    CanReceiveCommands = false
                },
                new ParticipantSessionDescriptor
                {
                    SessionId = "ios-session",
                    Client = "Emby for iOS",
                    SupportsRemoteControl = true,
                    IsOnline = true,
                    HasActiveWebSocket = true,
                    IsDormant = false,
                    CanReceiveCommands = true
                }
            };

            var rows = ParticipantInfoProjector.Project(
                participants,
                readyUserIds: new[] { "same-user" },
                masterSessionId: "vidhub-session",
                sessions);

            Assert.Equal(2, rows.Count);
            Assert.True(rows[0].IsHost);
            Assert.Equal("VidHub", rows[0].Client);
            Assert.False(rows[0].SupportsRemoteControl);
            Assert.True(rows[0].IsOnline);
            Assert.False(rows[0].CanReceiveCommands);
            Assert.False(rows[1].IsHost);
            Assert.Equal("Emby for iOS", rows[1].Client);
            Assert.True(rows[1].SupportsRemoteControl);
            Assert.True(rows[1].HasActiveWebSocket);
            Assert.True(rows[1].CanReceiveCommands);
            Assert.All(rows, row => Assert.True(row.IsReady));
        }

        [Fact]
        public void RetainedSessionIsReportedAsOfflineDormantAndNotCommandable()
        {
            var retained = Participant("viewer", "ios-session", "Viewer", 10);
            retained.IsPaused = true;
            var rows = ParticipantInfoProjector.Project(
                new[] { retained },
                readyUserIds: Array.Empty<string>(),
                masterSessionId: null,
                new[]
                {
                    new ParticipantSessionDescriptor
                    {
                        SessionId = "ios-session",
                        Client = "Emby for iOS",
                        IsOnline = false,
                        IsDormant = true,
                        CanReceiveCommands = false
                    }
                });

            var row = Assert.Single(rows);
            Assert.False(row.IsOnline);
            Assert.True(row.IsDormant);
            Assert.False(row.CanReceiveCommands);
            Assert.True(row.IsPaused);
        }

        private static PartyParticipant Participant(
            string userId,
            string sessionId,
            string userName,
            int activitySecond)
        {
            return new PartyParticipant
            {
                UserId = userId,
                SessionId = sessionId,
                UserName = userName,
                LastActivityAt = new DateTime(
                    2026,
                    8,
                    17,
                    1,
                    0,
                    activitySecond,
                    DateTimeKind.Utc)
            };
        }
    }
}
