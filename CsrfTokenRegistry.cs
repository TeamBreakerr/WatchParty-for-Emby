using System;
using System.Collections.Generic;
using System.Linq;

namespace WatchPartyForEmby
{
    public enum CsrfTokenValidation
    {
        Valid,
        MissingOrExpired,
        IpAddressMismatch
    }

    /// <summary>
    /// Stores reusable CSRF tokens and applies their expiration and client binding.
    /// </summary>
    public sealed class CsrfTokenRegistry
    {
        private readonly Dictionary<string, CsrfToken> _tokens =
            new Dictionary<string, CsrfToken>(StringComparer.Ordinal);
        private readonly object _syncRoot = new object();

        public void Add(CsrfToken token)
        {
            if (token == null || string.IsNullOrEmpty(token.Token))
            {
                throw new ArgumentException("A CSRF token value is required.", nameof(token));
            }

            lock (_syncRoot)
            {
                _tokens[token.Token] = token;
            }
        }

        public CsrfTokenValidation Validate(string token, string ipAddress)
        {
            if (string.IsNullOrEmpty(token))
            {
                return CsrfTokenValidation.MissingOrExpired;
            }

            lock (_syncRoot)
            {
                if (!_tokens.TryGetValue(token, out var storedToken))
                {
                    return CsrfTokenValidation.MissingOrExpired;
                }

                if (!storedToken.IsValid())
                {
                    _tokens.Remove(token);
                    return CsrfTokenValidation.MissingOrExpired;
                }

                return string.Equals(storedToken.IpAddress, ipAddress, StringComparison.Ordinal)
                    ? CsrfTokenValidation.Valid
                    : CsrfTokenValidation.IpAddressMismatch;
            }
        }

        public int RemoveExpired()
        {
            lock (_syncRoot)
            {
                var expiredTokens = _tokens
                    .Where(entry => !entry.Value.IsValid())
                    .Select(entry => entry.Key)
                    .ToList();

                foreach (var expiredToken in expiredTokens)
                {
                    _tokens.Remove(expiredToken);
                }

                return expiredTokens.Count;
            }
        }

        public void Clear()
        {
            lock (_syncRoot)
            {
                _tokens.Clear();
            }
        }
    }
}
