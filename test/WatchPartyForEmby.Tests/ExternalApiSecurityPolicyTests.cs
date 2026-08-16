using System;
using System.Net;
using Xunit;

namespace WatchPartyForEmby.Tests
{
    public sealed class ExternalApiSecurityPolicyTests
    {
        [Theory]
        [InlineData("POST", "/api/auth/login")]
        [InlineData("GET", "/api/auth/user")]
        [InlineData("GET", "/api/config/external-url")]
        [InlineData("GET", "/api/parties")]
        public void DocumentedPublicEndpointsDoNotRequireCredentials(string method, string path)
        {
            var request = Request(method, path);

            Assert.Equal(ExternalApiSecurityDecision.Allow, ExternalApiSecurityPolicy.Evaluate(request));
        }

        [Theory]
        [InlineData("GET", "/api/auth/login")]
        [InlineData("POST", "/api/auth/user")]
        [InlineData("POST", "/api/config/external-url")]
        [InlineData("POST", "/api/parties")]
        [InlineData("GET", "/api/parties/")]
        [InlineData("GET", "/api/parties/delete")]
        public void MethodOrPathLookalikesRemainProtected(string method, string path)
        {
            var request = Request(method, path);

            Assert.Equal(
                ExternalApiSecurityDecision.AuthenticationRequired,
                ExternalApiSecurityPolicy.Evaluate(request));
        }

        [Fact]
        public void ValidSessionAuthorizesProtectedGet()
        {
            var request = Request("GET", "/api/emby/Users");
            request.HasValidSession = true;

            Assert.Equal(ExternalApiSecurityDecision.Allow, ExternalApiSecurityPolicy.Evaluate(request));
        }

        [Fact]
        public void LoopbackReverseProxyWithAuthenticatedUserAuthorizesProtectedGet()
        {
            var request = Request("GET", "/api/emby/Users");
            request.UseReverseProxy = true;
            request.RemoteAddress = IPAddress.Loopback;
            request.AuthentikUsername = "alice";

            Assert.Equal(ExternalApiSecurityDecision.Allow, ExternalApiSecurityPolicy.Evaluate(request));
        }

        [Fact]
        public void ProtectedPostWithCredentialsStillRequiresCsrfToken()
        {
            var request = Request("POST", "/api/parties/create");
            request.HasValidSession = true;
            request.CsrfProtectionEnabled = true;

            Assert.Equal(
                ExternalApiSecurityDecision.CsrfTokenRequired,
                ExternalApiSecurityPolicy.Evaluate(request));
        }

        [Fact]
        public void ManualWaitingRoomStartIsAnAuthenticatedCsrfProtectedOperation()
        {
            var unauthenticated = Request("POST", "/api/parties/start");
            Assert.Equal(
                ExternalApiSecurityDecision.AuthenticationRequired,
                ExternalApiSecurityPolicy.Evaluate(unauthenticated));

            var authenticated = Request("POST", "/api/parties/start");
            authenticated.HasValidSession = true;
            authenticated.CsrfProtectionEnabled = true;
            Assert.Equal(
                ExternalApiSecurityDecision.CsrfTokenRequired,
                ExternalApiSecurityPolicy.Evaluate(authenticated));

            authenticated.HasValidCsrfToken = true;
            Assert.Equal(
                ExternalApiSecurityDecision.Allow,
                ExternalApiSecurityPolicy.Evaluate(authenticated));
        }

        [Fact]
        public void AllowedEmbyProxyRouteAcceptsExactReadOnlyEndpoint()
        {
            Assert.True(ExternalApiSecurityPolicy.IsAllowedEmbyProxyRoute("GET", "/api/emby/Users"));
        }

        [Theory]
        [InlineData("/api/emby/Library/MediaFolders")]
        [InlineData("/api/emby/Items")]
        [InlineData("/api/emby/Shows/1234abcd/Seasons")]
        [InlineData("/api/emby/Shows/series-01_TEST/Episodes")]
        public void AllowedEmbyProxyRoutesCoverOnlyRequiredCatalogReads(string path)
        {
            Assert.True(ExternalApiSecurityPolicy.IsAllowedEmbyProxyRoute("GET", path));
        }

        [Theory]
        [InlineData("POST", "/api/emby/Users")]
        [InlineData("DELETE", "/api/emby/Items")]
        [InlineData("GET", "/api/emby")]
        [InlineData("GET", "/api/emby/")]
        [InlineData("GET", "/api/emby/Users/")]
        [InlineData("GET", "/api/emby/Users/administrator")]
        [InlineData("GET", "/api/emby/Items/secret")]
        [InlineData("GET", "/api/emby/System/Info")]
        [InlineData("GET", "/api/emby/Shows/series/Seasons/extra")]
        [InlineData("GET", "/api/emby/Shows/series/Items")]
        [InlineData("GET", "/api/emby/Shows/../Seasons")]
        [InlineData("GET", "/api/emby/Shows/%2e%2e/Seasons")]
        [InlineData("GET", "/api/emby/Shows/series%2Fsecret/Seasons")]
        [InlineData("GET", "/api/emby/Shows/series.name/Seasons")]
        [InlineData("GET", "/api/emby/shows/series/Seasons")]
        public void EmbyProxyRejectsWritesAndAnythingOutsideExactWhitelist(string method, string path)
        {
            Assert.False(ExternalApiSecurityPolicy.IsAllowedEmbyProxyRoute(method, path));
        }

        [Fact]
        public void EmbyShowIdLengthIsBounded()
        {
            Assert.True(ExternalApiSecurityPolicy.IsAllowedEmbyProxyRoute(
                "GET",
                $"/api/emby/Shows/{new string('a', 128)}/Seasons"));
            Assert.False(ExternalApiSecurityPolicy.IsAllowedEmbyProxyRoute(
                "GET",
                $"/api/emby/Shows/{new string('a', 129)}/Seasons"));
        }

        [Theory]
        [InlineData(false, "127.0.0.1", "alice")]
        [InlineData(true, "203.0.113.10", "alice")]
        [InlineData(true, "127.0.0.1", "")]
        [InlineData(true, "127.0.0.1", "   ")]
        public void SsoHeadersAreRejectedUnlessTheyComeThroughTheLocalReverseProxy(
            bool useReverseProxy,
            string remoteAddress,
            string username)
        {
            var request = Request("GET", "/api/emby/Users");
            request.UseReverseProxy = useReverseProxy;
            request.RemoteAddress = IPAddress.Parse(remoteAddress);
            request.AuthentikUsername = username;

            Assert.Equal(
                ExternalApiSecurityDecision.AuthenticationRequired,
                ExternalApiSecurityPolicy.Evaluate(request));
        }

        [Fact]
        public void Ipv4MappedLoopbackIsAcceptedForTrustedReverseProxy()
        {
            var request = Request("GET", "/api/emby/Users");
            request.UseReverseProxy = true;
            request.RemoteAddress = IPAddress.Parse("::ffff:127.0.0.1");
            request.AuthentikUsername = "alice";

            Assert.Equal(ExternalApiSecurityDecision.Allow, ExternalApiSecurityPolicy.Evaluate(request));
        }

        [Theory]
        [InlineData(true, true)]
        [InlineData(false, false)]
        public void ProtectedPostAllowsValidCsrfOrExplicitlyDisabledProtection(
            bool csrfProtectionEnabled,
            bool hasValidCsrfToken)
        {
            var request = Request("POST", "/api/parties/create");
            request.HasValidSession = true;
            request.CsrfProtectionEnabled = csrfProtectionEnabled;
            request.HasValidCsrfToken = hasValidCsrfToken;

            Assert.Equal(ExternalApiSecurityDecision.Allow, ExternalApiSecurityPolicy.Evaluate(request));
        }

        [Theory]
        [InlineData("http://localhost")]
        [InlineData("https://localhost:8443")]
        [InlineData("http://127.0.0.1:3000")]
        [InlineData("http://[::1]:3000")]
        public void DefaultCorsAllowsOnlyExactLoopbackOrigins(string origin)
        {
            Assert.True(ExternalApiSecurityPolicy.IsAllowedDefaultCorsOrigin(origin));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("http://localhost.evil.example")]
        [InlineData("http://localhost.")]
        [InlineData("http://127.0.0.1.evil.example")]
        [InlineData("http://127.0.0.1.")]
        [InlineData("http://2130706433")]
        [InlineData("http://0177.0.0.1")]
        [InlineData("http://example.com/?next=localhost")]
        [InlineData("file://localhost/tmp/page.html")]
        [InlineData("http://user@localhost")]
        [InlineData("http://localhost/path")]
        [InlineData("http://localhost?query=1")]
        [InlineData(" http://localhost")]
        [InlineData("http://localhost ")]
        public void DefaultCorsRejectsLookalikesAndNonOriginUrls(string origin)
        {
            Assert.False(ExternalApiSecurityPolicy.IsAllowedDefaultCorsOrigin(origin));
        }

        private static ExternalApiSecurityRequest Request(string method, string path)
        {
            return new ExternalApiSecurityRequest
            {
                Method = method,
                Path = path,
                RemoteAddress = IPAddress.Parse("203.0.113.10")
            };
        }
    }
}
