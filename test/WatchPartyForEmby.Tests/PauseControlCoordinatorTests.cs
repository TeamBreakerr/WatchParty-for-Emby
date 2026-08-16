using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class PauseControlModeParserTests
    {
        [Theory]
        [InlineData("Anyone", PauseControlMode.Anyone)]
        [InlineData(" anyone ", PauseControlMode.Anyone)]
        [InlineData("Host", PauseControlMode.Host)]
        [InlineData("HOSTONLY", PauseControlMode.Host)]
        [InlineData("Master", PauseControlMode.Host)]
        [InlineData("MasterOnly", PauseControlMode.Host)]
        [InlineData("Disabled", PauseControlMode.Host)]
        [InlineData("Vote", PauseControlMode.Vote)]
        public void CanonicalAndLegacyValuesHaveTypedSemantics(
            string value,
            PauseControlMode expected)
        {
            Assert.True(PauseControlModeParser.TryParse(value, out var parsed));
            Assert.Equal(expected, parsed);
            Assert.Equal(expected, PauseControlModeParser.Parse(value));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Nobody")]
        public void MissingOrUnknownValuesFallBackToAnyone(string value)
        {
            Assert.False(PauseControlModeParser.TryParse(value, out var parsed));
            Assert.Equal(PauseControlMode.Anyone, parsed);
            Assert.Equal(PauseControlMode.Anyone, PauseControlModeParser.Parse(value));
        }
    }

    public sealed class PauseControlCoordinatorTests
    {
        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public void AnyoneCanBroadcastEitherPlaybackState(
            bool initialIsPaused,
            bool requestedIsPaused)
        {
            var coordinator = CreateParty(initialIsPaused);

            var decision = coordinator.HandleRequest(
                PartyId,
                PauseControlMode.Anyone,
                "viewer-a",
                actorIsHost: false,
                requestedIsPaused,
                activeParticipantCount: 3);

            AssertDecision(
                decision,
                PauseControlDecisionKind.Broadcast,
                requestedIsPaused,
                voteCount: 0,
                requiredVotes: 0);
        }

        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public void HostModeRejectsViewerAndKeepsTheAuthoritativeState(
            bool initialIsPaused,
            bool requestedIsPaused)
        {
            var coordinator = CreateParty(initialIsPaused);

            var decision = coordinator.HandleRequest(
                PartyId,
                PauseControlMode.Host,
                "viewer-a",
                actorIsHost: false,
                requestedIsPaused,
                activeParticipantCount: 3);

            AssertDecision(
                decision,
                PauseControlDecisionKind.RejectActor,
                initialIsPaused,
                voteCount: 0,
                requiredVotes: 0);
        }

        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public void HostCanBroadcastEitherPlaybackState(
            bool initialIsPaused,
            bool requestedIsPaused)
        {
            var coordinator = CreateParty(initialIsPaused);

            var decision = coordinator.HandleRequest(
                PartyId,
                PauseControlMode.Host,
                "host-user",
                actorIsHost: true,
                requestedIsPaused,
                activeParticipantCount: 3);

            AssertDecision(
                decision,
                PauseControlDecisionKind.Broadcast,
                requestedIsPaused,
                voteCount: 0,
                requiredVotes: 0);
        }

        [Fact]
        public void VoteModeRequiresAStrictMajorityBeforePausing()
        {
            var coordinator = CreateParty(initialIsPaused: false);

            var first = Vote(coordinator, "viewer-a", requestedIsPaused: true, participants: 4);
            var second = Vote(coordinator, "viewer-b", requestedIsPaused: true, participants: 4);
            var third = Vote(coordinator, "host-user", requestedIsPaused: true, participants: 4);

            AssertDecision(first, PauseControlDecisionKind.WaitForVotes, false, 1, 3);
            AssertDecision(second, PauseControlDecisionKind.WaitForVotes, false, 2, 3);
            AssertDecision(third, PauseControlDecisionKind.Broadcast, true, 3, 3);
        }

        [Fact]
        public void RepeatedVotesFromTheSameUserAreIdempotent()
        {
            var coordinator = CreateParty(initialIsPaused: false);

            var first = Vote(coordinator, "viewer-a", requestedIsPaused: true, participants: 3);
            var duplicate = Vote(coordinator, "viewer-a", requestedIsPaused: true, participants: 3);

            AssertDecision(first, PauseControlDecisionKind.WaitForVotes, false, 1, 2);
            AssertDecision(duplicate, PauseControlDecisionKind.WaitForVotes, false, 1, 2);
            Assert.False(first.IsDuplicateVote);
            Assert.True(duplicate.IsDuplicateVote);
        }

        [Fact]
        public void SingleUnpauseVoteCannotClearVotesOrResumeEveryone()
        {
            var coordinator = CreateParty(initialIsPaused: true);

            var first = Vote(coordinator, "viewer-a", requestedIsPaused: false, participants: 3);
            var duplicate = Vote(coordinator, "viewer-a", requestedIsPaused: false, participants: 3);
            var passing = Vote(coordinator, "viewer-b", requestedIsPaused: false, participants: 3);

            AssertDecision(first, PauseControlDecisionKind.WaitForVotes, true, 1, 2);
            AssertDecision(duplicate, PauseControlDecisionKind.WaitForVotes, true, 1, 2);
            AssertDecision(passing, PauseControlDecisionKind.Broadcast, false, 2, 2);
        }

        [Fact]
        public void ReturningToTheCurrentStateWithdrawsOnlyTheActorsVote()
        {
            var coordinator = CreateParty(initialIsPaused: false);

            Vote(coordinator, "viewer-a", requestedIsPaused: true, participants: 4);
            Vote(coordinator, "viewer-b", requestedIsPaused: true, participants: 4);

            var withdrawal = Vote(
                coordinator,
                "viewer-a",
                requestedIsPaused: false,
                participants: 4);
            var thirdUser = Vote(coordinator, "viewer-c", requestedIsPaused: true, participants: 4);
            var fourthUser = Vote(coordinator, "viewer-d", requestedIsPaused: true, participants: 4);

            AssertDecision(withdrawal, PauseControlDecisionKind.RejectActor, false, 1, 3);
            AssertDecision(thirdUser, PauseControlDecisionKind.WaitForVotes, false, 2, 3);
            AssertDecision(fourthUser, PauseControlDecisionKind.Broadcast, true, 3, 3);
        }

        [Fact]
        public void LeavingThePartyRemovesOnlyThatMembersVote()
        {
            var coordinator = CreateParty(initialIsPaused: false);

            Vote(coordinator, "viewer-a", requestedIsPaused: true, participants: 3);

            Assert.True(coordinator.RemoveMember(PartyId, "viewer-a"));
            Assert.False(coordinator.RemoveMember(PartyId, "viewer-a"));

            var next = Vote(coordinator, "viewer-b", requestedIsPaused: true, participants: 3);
            var passing = Vote(coordinator, "viewer-c", requestedIsPaused: true, participants: 3);

            AssertDecision(next, PauseControlDecisionKind.WaitForVotes, false, 1, 2);
            AssertDecision(passing, PauseControlDecisionKind.Broadcast, true, 2, 2);
        }

        [Fact]
        public void ResetClearsVotesAndSetsTheAuthoritativeState()
        {
            var coordinator = CreateParty(initialIsPaused: false);
            Vote(coordinator, "viewer-a", requestedIsPaused: true, participants: 3);

            coordinator.ResetParty(PartyId, isPaused: true);

            var decision = Vote(
                coordinator,
                "viewer-b",
                requestedIsPaused: false,
                participants: 3);

            AssertDecision(decision, PauseControlDecisionKind.WaitForVotes, true, 1, 2);
        }

        [Fact]
        public void ObservedMasterPlaybackOverridesAStaleResetState()
        {
            var coordinator = CreateParty(initialIsPaused: true);

            coordinator.ObserveAuthoritativeState(PartyId, isPaused: false);

            var decision = coordinator.HandleRequest(
                PartyId,
                PauseControlMode.Host,
                "viewer-a",
                actorIsHost: false,
                requestedIsPaused: true,
                activeParticipantCount: 2);

            AssertDecision(
                decision,
                PauseControlDecisionKind.RejectActor,
                expectedAuthoritativePauseState: false,
                voteCount: 0,
                requiredVotes: 0);
        }

        [Fact]
        public void RepeatedAuthoritativeProgressDoesNotErasePendingVotes()
        {
            var coordinator = CreateParty(initialIsPaused: false);
            Vote(coordinator, "viewer-a", requestedIsPaused: true, participants: 4);

            Assert.False(coordinator.ObserveAuthoritativeState(
                PartyId,
                isPaused: false));

            var secondVote = Vote(
                coordinator,
                "viewer-b",
                requestedIsPaused: true,
                participants: 4);
            AssertDecision(
                secondVote,
                PauseControlDecisionKind.WaitForVotes,
                expectedAuthoritativePauseState: false,
                voteCount: 2,
                requiredVotes: 3);
        }

        [Fact]
        public void ClearingAPartyRemovesItsAuthoritativeStateAndVotes()
        {
            var coordinator = CreateParty(initialIsPaused: true);
            Vote(coordinator, "viewer-a", requestedIsPaused: false, participants: 3);

            Assert.True(coordinator.ClearParty(PartyId));
            Assert.False(coordinator.ClearParty(PartyId));

            var decision = Vote(
                coordinator,
                "viewer-b",
                requestedIsPaused: true,
                participants: 3);
            AssertDecision(decision, PauseControlDecisionKind.WaitForVotes, false, 1, 2);
        }

        [Fact]
        public void ChangingModesCannotCarryOldVotesIntoANewPolicy()
        {
            var coordinator = CreateParty(initialIsPaused: false);
            Vote(coordinator, "viewer-a", requestedIsPaused: true, participants: 3);

            coordinator.HandleRequest(
                PartyId,
                PauseControlMode.Host,
                "viewer-b",
                actorIsHost: false,
                requestedIsPaused: true,
                activeParticipantCount: 3);

            var decision = Vote(
                coordinator,
                "viewer-b",
                requestedIsPaused: true,
                participants: 3);

            AssertDecision(decision, PauseControlDecisionKind.WaitForVotes, false, 1, 2);
        }

        [Fact]
        public async Task ConcurrentDuplicateVotesStillCountOnce()
        {
            var coordinator = CreateParty(initialIsPaused: false);
            var decisions = new ConcurrentBag<PauseControlDecision>();

            await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() =>
                decisions.Add(Vote(
                    coordinator,
                    "viewer-a",
                    requestedIsPaused: true,
                    participants: 3)))));

            Assert.Equal(100, decisions.Count);
            Assert.All(decisions, decision =>
                AssertDecision(decision, PauseControlDecisionKind.WaitForVotes, false, 1, 2));

            var passing = Vote(
                coordinator,
                "viewer-b",
                requestedIsPaused: true,
                participants: 3);
            AssertDecision(passing, PauseControlDecisionKind.Broadcast, true, 2, 2);
        }

        [Fact]
        public void PartiesHaveIndependentVoteState()
        {
            var coordinator = new PauseControlCoordinator();
            coordinator.ResetParty("party-a", isPaused: false);
            coordinator.ResetParty("party-b", isPaused: false);

            Vote(coordinator, "viewer-a", true, 3, "party-a");
            var partyB = Vote(coordinator, "viewer-b", true, 3, "party-b");
            var partyA = Vote(coordinator, "viewer-b", true, 3, "party-a");

            AssertDecision(partyB, PauseControlDecisionKind.WaitForVotes, false, 1, 2);
            AssertDecision(partyA, PauseControlDecisionKind.Broadcast, true, 2, 2);
        }

        private const string PartyId = "party-1";

        private static PauseControlCoordinator CreateParty(bool initialIsPaused)
        {
            var coordinator = new PauseControlCoordinator();
            coordinator.ResetParty(PartyId, initialIsPaused);
            return coordinator;
        }

        private static PauseControlDecision Vote(
            PauseControlCoordinator coordinator,
            string actorUserId,
            bool requestedIsPaused,
            int participants,
            string partyId = PartyId)
        {
            return coordinator.HandleRequest(
                partyId,
                PauseControlMode.Vote,
                actorUserId,
                actorIsHost: actorUserId == "host-user",
                requestedIsPaused,
                participants);
        }

        private static void AssertDecision(
            PauseControlDecision decision,
            PauseControlDecisionKind expectedKind,
            bool expectedAuthoritativePauseState,
            int voteCount,
            int requiredVotes)
        {
            Assert.Equal(expectedKind, decision.Kind);
            Assert.Equal(expectedAuthoritativePauseState, decision.AuthoritativeIsPaused);
            Assert.Equal(voteCount, decision.VoteCount);
            Assert.Equal(requiredVotes, decision.RequiredVotes);
        }
    }
}
