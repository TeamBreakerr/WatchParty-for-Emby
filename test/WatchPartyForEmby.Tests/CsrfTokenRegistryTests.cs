using System;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class CsrfTokenRegistryTests
    {
        [Fact]
        public void ValidTokenCanBeReusedUntilItExpires()
        {
            var registry = new CsrfTokenRegistry();
            registry.Add(new CsrfToken
            {
                Token = "csrf-token",
                IpAddress = "127.0.0.1",
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(5)
            });

            Assert.Equal(
                CsrfTokenValidation.Valid,
                registry.Validate("csrf-token", "127.0.0.1"));
            Assert.Equal(
                CsrfTokenValidation.Valid,
                registry.Validate("csrf-token", "127.0.0.1"));
        }

        [Fact]
        public void TokenCannotBeUsedFromAnotherClientAddress()
        {
            var registry = RegistryWithToken("csrf-token", DateTime.UtcNow.AddMinutes(5));

            Assert.Equal(
                CsrfTokenValidation.IpAddressMismatch,
                registry.Validate("csrf-token", "203.0.113.10"));
            Assert.Equal(
                CsrfTokenValidation.Valid,
                registry.Validate("csrf-token", "127.0.0.1"));
        }

        [Fact]
        public void ExpiredTokenIsRejectedAndRemoved()
        {
            var registry = RegistryWithToken("expired-token", DateTime.UtcNow.AddMinutes(-1));

            Assert.Equal(
                CsrfTokenValidation.MissingOrExpired,
                registry.Validate("expired-token", "127.0.0.1"));
            Assert.Equal(
                CsrfTokenValidation.MissingOrExpired,
                registry.Validate("expired-token", "127.0.0.1"));
        }

        [Fact]
        public void CleanupRemovesOnlyExpiredTokens()
        {
            var registry = RegistryWithToken("valid-token", DateTime.UtcNow.AddMinutes(5));
            registry.Add(Token("expired-token", DateTime.UtcNow.AddMinutes(-1)));

            Assert.Equal(1, registry.RemoveExpired());
            Assert.Equal(
                CsrfTokenValidation.Valid,
                registry.Validate("valid-token", "127.0.0.1"));
            Assert.Equal(
                CsrfTokenValidation.MissingOrExpired,
                registry.Validate("expired-token", "127.0.0.1"));
        }

        private static CsrfTokenRegistry RegistryWithToken(string value, DateTime expiresAt)
        {
            var registry = new CsrfTokenRegistry();
            registry.Add(Token(value, expiresAt));
            return registry;
        }

        private static CsrfToken Token(string value, DateTime expiresAt)
        {
            return new CsrfToken
            {
                Token = value,
                IpAddress = "127.0.0.1",
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = expiresAt
            };
        }
    }
}
