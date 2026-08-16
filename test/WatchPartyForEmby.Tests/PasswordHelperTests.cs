using System;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class PasswordHelperTests
    {
        [Fact]
        public void CurrentPasswordHashAcceptsOnlyTheOriginalPassword()
        {
            var hash = PasswordHelper.HashPassword("correct horse battery staple");

            Assert.True(PasswordHelper.VerifyPassword("correct horse battery staple", hash));
            Assert.False(PasswordHelper.VerifyPassword("wrong", hash));
        }

        [Fact]
        public void LegacySha256HashesRemainReadableDuringMigration()
        {
            string legacyHash;
            using (var sha256 = SHA256.Create())
            {
                legacyHash = Convert.ToBase64String(
                    sha256.ComputeHash(Encoding.UTF8.GetBytes("legacy-password")));
            }

            Assert.True(PasswordHelper.VerifyPassword("legacy-password", legacyHash));
            Assert.False(PasswordHelper.VerifyPassword("wrong", legacyHash));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not-base64")]
        public void MissingOrMalformedHashesAreRejected(string hash)
        {
            Assert.False(PasswordHelper.VerifyPassword("password", hash));
        }
    }
}
