using System.Collections.Generic;
using System;
using MediaBrowser.Controller.Net;
using WatchPartyForEmby.Api;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class WatchPartyAuthorizationPolicyTests
    {
        [Fact]
        public void RestrictedRoomAllowsOnlyListedUsersHostMasterOrAdministrator()
        {
            var party = Party("master", "host", "viewer");

            Assert.True(WatchPartyAuthorizationPolicy.CanAccessParty(party, "viewer", false));
            Assert.True(WatchPartyAuthorizationPolicy.CanAccessParty(party, "MASTER", false));
            Assert.True(WatchPartyAuthorizationPolicy.CanAccessParty(party, "host", false));
            Assert.True(WatchPartyAuthorizationPolicy.CanAccessParty(party, "outsider", true));
            Assert.False(WatchPartyAuthorizationPolicy.CanAccessParty(party, "outsider", false));
        }

        [Fact]
        public void UnrestrictedRoomAllowsEveryAuthenticatedUser()
        {
            var party = Party("master", "master");

            Assert.True(WatchPartyAuthorizationPolicy.CanAccessParty(party, "viewer", false));
            Assert.False(WatchPartyAuthorizationPolicy.CanAccessParty(party, null, false));
        }

        [Fact]
        public void OnlyMasterHostOrAdministratorCanStartRoom()
        {
            var party = Party("master", "host", "viewer");

            Assert.True(WatchPartyAuthorizationPolicy.CanStartParty(party, "master", false));
            Assert.True(WatchPartyAuthorizationPolicy.CanStartParty(party, "HOST", false));
            Assert.True(WatchPartyAuthorizationPolicy.CanStartParty(party, "admin", true));
            Assert.False(WatchPartyAuthorizationPolicy.CanStartParty(party, "viewer", false));
        }

        [Fact]
        public void OnlyMasterHostOrAdministratorCanForceRoomSynchronization()
        {
            var party = Party("master", "host", "viewer");

            Assert.True(WatchPartyAuthorizationPolicy.CanSynchronizeParty(party, "master", false));
            Assert.True(WatchPartyAuthorizationPolicy.CanSynchronizeParty(party, "HOST", false));
            Assert.True(WatchPartyAuthorizationPolicy.CanSynchronizeParty(party, "admin", true));
            Assert.False(WatchPartyAuthorizationPolicy.CanSynchronizeParty(party, "viewer", false));
        }

        [Fact]
        public void AdministratorCanInspectPasswordProtectedRoomWithoutSupplyingItsPassword()
        {
            var party = Party("master", "host");
            party.PasswordHash = PasswordHelper.HashPassword("secret");

            Assert.True(WatchPartyAuthorizationPolicy.SatisfiesPartyPassword(
                party,
                password: null,
                isAdministrator: true));
            Assert.False(WatchPartyAuthorizationPolicy.SatisfiesPartyPassword(
                party,
                password: null,
                isAdministrator: false));
            Assert.True(WatchPartyAuthorizationPolicy.SatisfiesPartyPassword(
                party,
                password: "secret",
                isAdministrator: false));
        }

        [Fact]
        public void ReadyRequiresBothRoomAccessAndAnActivePartySession()
        {
            var party = Party("master", "host", "viewer");

            Assert.True(WatchPartyAuthorizationPolicy.CanSetReady(
                party,
                "viewer",
                isAdministrator: false,
                hasActivePartySession: true));
            Assert.False(WatchPartyAuthorizationPolicy.CanSetReady(
                party,
                "viewer",
                isAdministrator: false,
                hasActivePartySession: false));
            Assert.False(WatchPartyAuthorizationPolicy.CanSetReady(
                party,
                "outsider",
                isAdministrator: false,
                hasActivePartySession: true));
        }

        [Theory]
        [InlineData(typeof(WatchPartyListRequest))]
        [InlineData(typeof(PartyParticipantsRequest))]
        [InlineData(typeof(SetReadyRequest))]
        [InlineData(typeof(StartPartyRequest))]
        [InlineData(typeof(PartyLaunchTargetsRequest))]
        [InlineData(typeof(SynchronizePartyRequest))]
        [InlineData(typeof(GetUsersRequest))]
        public void StateAndIdentityEndpointsRequireAnAuthenticatedEmbySession(Type requestType)
        {
            Assert.NotNull(Attribute.GetCustomAttribute(
                requestType,
                typeof(AuthenticatedAttribute)));
        }

        private static WatchPartyItem Party(
            string masterUserId,
            string hostUserId,
            params string[] allowedUsers)
        {
            return new WatchPartyItem
            {
                MasterUserId = masterUserId,
                HostUserId = hostUserId,
                AllowedUserIds = new List<string>(allowedUsers)
            };
        }
    }
}
