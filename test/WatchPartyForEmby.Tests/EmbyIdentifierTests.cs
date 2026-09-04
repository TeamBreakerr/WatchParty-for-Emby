using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class EmbyIdentifierTests
    {
        [Fact]
        public void TheTwoShapesEmbyUsesForOneUserMustMatch()
        {
            // SessionInfo.UserId carries the 32-character form; the API service
            // resolves the authenticated user and hands over User.Id.ToString(),
            // which inserts hyphens. Comparing those ordinally resolved no session,
            // so every explicit Web seek notification was discarded.
            Assert.True(EmbyIdentifier.Matches(
                "947cab2816db4b3a98a4bfdc21480fa6",
                "947cab28-16db-4b3a-98a4-bfdc21480fa6"));

            Assert.True(EmbyIdentifier.Matches(
                "947cab28-16db-4b3a-98a4-bfdc21480fa6",
                "947cab2816db4b3a98a4bfdc21480fa6"));
        }

        [Fact]
        public void CaseAndBraceSpellingsOfTheSameIdentityStillMatch()
        {
            Assert.True(EmbyIdentifier.Matches(
                "947CAB2816DB4B3A98A4BFDC21480FA6",
                "947cab28-16db-4b3a-98a4-bfdc21480fa6"));

            Assert.True(EmbyIdentifier.Matches(
                "{947cab28-16db-4b3a-98a4-bfdc21480fa6}",
                "947cab2816db4b3a98a4bfdc21480fa6"));
        }

        [Fact]
        public void DifferentIdentitiesMustNotMatch()
        {
            Assert.False(EmbyIdentifier.Matches(
                "947cab2816db4b3a98a4bfdc21480fa6",
                "bd65e810cb10451f94bbd371863bb51b"));

            Assert.False(EmbyIdentifier.Matches(
                "947cab28-16db-4b3a-98a4-bfdc21480fa6",
                "bd65e810-cb10-451f-94bb-d371863bb51b"));
        }

        [Fact]
        public void NonGuidIdentifiersKeepPlainTextEquality()
        {
            // Item identifiers are not GUIDs on every deployment; equal text must
            // still match, and unequal text must still be rejected.
            Assert.True(EmbyIdentifier.Matches("2160375", "2160375"));
            Assert.False(EmbyIdentifier.Matches("2160375", "2131360"));
        }

        [Fact]
        public void AnAbsentIdentifierNeverMatches()
        {
            Assert.False(EmbyIdentifier.Matches(null, "2160375"));
            Assert.False(EmbyIdentifier.Matches("2160375", null));
            Assert.False(EmbyIdentifier.Matches("   ", "2160375"));
            Assert.False(EmbyIdentifier.Matches(null, null));
        }
    }
}
