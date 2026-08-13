using System;

namespace WatchPartyForEmby
{
    public static class EmbyServerAddress
    {
        private const string DefaultBaseUrl = "http://localhost:8096";

        public static string GetBaseUrl(string configuredUrl)
        {
            var baseUrl = string.IsNullOrWhiteSpace(configuredUrl)
                ? DefaultBaseUrl
                : configuredUrl.Trim();

            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException("The Emby server URL must be an absolute HTTP or HTTPS URL.", nameof(configuredUrl));
            }

            return baseUrl.TrimEnd('/');
        }

        public static string Build(string configuredUrl, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return GetBaseUrl(configuredUrl);
            }

            return GetBaseUrl(configuredUrl) + "/" + relativePath.TrimStart('/');
        }
    }
}
