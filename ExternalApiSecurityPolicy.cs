using System;
using System.Net;
using System.Text.RegularExpressions;

namespace WatchPartyForEmby
{
    public enum ExternalApiSecurityDecision
    {
        Allow,
        AuthenticationRequired,
        CsrfTokenRequired
    }

    public sealed class ExternalApiSecurityRequest
    {
        public string Method { get; set; }
        public string Path { get; set; }
        public bool HasValidSession { get; set; }
        public bool UseReverseProxy { get; set; }
        public IPAddress RemoteAddress { get; set; }
        public string AuthentikUsername { get; set; }
        public bool CsrfProtectionEnabled { get; set; }
        public bool HasValidCsrfToken { get; set; }
    }

    /// <summary>
    /// Defines the authentication boundary for the external management API.
    /// </summary>
    public static class ExternalApiSecurityPolicy
    {
        private static readonly Regex ShowCatalogRoute = new Regex(
            @"^/api/emby/Shows/[A-Za-z0-9_-]{1,128}/(?:Seasons|Episodes)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static ExternalApiSecurityDecision Evaluate(ExternalApiSecurityRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (IsPublicEndpoint(request.Method, request.Path))
            {
                return ExternalApiSecurityDecision.Allow;
            }

            if (!request.HasValidSession && !IsTrustedSso(request))
            {
                return ExternalApiSecurityDecision.AuthenticationRequired;
            }

            if (string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)
                && request.CsrfProtectionEnabled
                && !request.HasValidCsrfToken)
            {
                return ExternalApiSecurityDecision.CsrfTokenRequired;
            }

            return ExternalApiSecurityDecision.Allow;
        }

        public static bool IsTrustedSso(ExternalApiSecurityRequest request)
        {
            if (request == null || request.RemoteAddress == null)
            {
                return false;
            }

            var address = request.RemoteAddress.IsIPv4MappedToIPv6
                ? request.RemoteAddress.MapToIPv4()
                : request.RemoteAddress;

            return request.UseReverseProxy
                && IPAddress.IsLoopback(address)
                && !string.IsNullOrWhiteSpace(request.AuthentikUsername);
        }

        public static bool IsPublicEndpoint(string method, string path)
        {
            return IsRoute(method, path, "POST", "/api/auth/login")
                || IsRoute(method, path, "GET", "/api/auth/user")
                || IsRoute(method, path, "GET", "/api/config/external-url")
                || IsRoute(method, path, "GET", "/api/parties");
        }

        public static bool IsAllowedEmbyProxyRoute(string method, string path)
        {
            if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return string.Equals(path, "/api/emby/Library/MediaFolders", StringComparison.Ordinal)
                || string.Equals(path, "/api/emby/Users", StringComparison.Ordinal)
                || string.Equals(path, "/api/emby/Items", StringComparison.Ordinal)
                || (!string.IsNullOrEmpty(path) && ShowCatalogRoute.IsMatch(path));
        }

        public static bool IsAllowedDefaultCorsOrigin(string origin)
        {
            if (string.IsNullOrEmpty(origin)
                || !string.Equals(origin, origin.Trim(), StringComparison.Ordinal)
                || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                || (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                || !string.IsNullOrEmpty(uri.UserInfo)
                || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment)
                || !string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal))
            {
                return false;
            }

            return HasExactLoopbackAuthority(origin);
        }

        private static bool HasExactLoopbackAuthority(string origin)
        {
            var authorityStart = origin.IndexOf("://", StringComparison.Ordinal);
            if (authorityStart < 0)
            {
                return false;
            }

            authorityStart += 3;
            var authorityEnd = origin.IndexOfAny(new[] { '/', '?', '#' }, authorityStart);
            var authority = authorityEnd < 0
                ? origin.Substring(authorityStart)
                : origin.Substring(authorityStart, authorityEnd - authorityStart);
            if (authority.Length == 0)
            {
                return false;
            }

            string rawHost;
            string portSuffix;
            if (authority[0] == '[')
            {
                var closingBracket = authority.IndexOf(']');
                if (closingBracket < 0)
                {
                    return false;
                }

                rawHost = authority.Substring(0, closingBracket + 1);
                portSuffix = authority.Substring(closingBracket + 1);
            }
            else
            {
                var portSeparator = authority.LastIndexOf(':');
                rawHost = portSeparator < 0
                    ? authority
                    : authority.Substring(0, portSeparator);
                portSuffix = portSeparator < 0
                    ? string.Empty
                    : authority.Substring(portSeparator);

                if (rawHost.IndexOf(':') >= 0)
                {
                    return false;
                }
            }

            if (portSuffix.Length > 0
                && (portSuffix.Length == 1
                    || portSuffix[0] != ':'
                    || !int.TryParse(portSuffix.Substring(1), out _)))
            {
                return false;
            }

            return string.Equals(rawHost, "localhost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(rawHost, "127.0.0.1", StringComparison.Ordinal)
                || string.Equals(rawHost, "[::1]", StringComparison.Ordinal);
        }

        private static bool IsRoute(string method, string path, string expectedMethod, string expectedPath)
        {
            return string.Equals(method, expectedMethod, StringComparison.OrdinalIgnoreCase)
                && string.Equals(path, expectedPath, StringComparison.Ordinal);
        }
    }
}
