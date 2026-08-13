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
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment))
            {
                throw new ArgumentException(
                    "The Emby server URL must be an absolute HTTP or HTTPS URL without a query or fragment.",
                    nameof(configuredUrl));
            }

            return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }

        public static string Build(string configuredUrl, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return GetBaseUrl(configuredUrl);
            }

            var baseUri = new Uri(GetBaseUrl(configuredUrl) + "/", UriKind.Absolute);
            return new Uri(baseUri, relativePath.TrimStart('/')).ToString();
        }
    }
}
