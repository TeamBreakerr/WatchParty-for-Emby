using System;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Captures only settings baked into an HttpListener at construction time.
    /// </summary>
    public sealed class ExternalWebServerBinding : IEquatable<ExternalWebServerBinding>
    {
        private ExternalWebServerBinding(
            bool enabled,
            int port,
            string listenAddress,
            bool useReverseProxy,
            bool enableHttps)
        {
            Enabled = enabled;
            Port = port;
            ListenAddress = listenAddress;
            UseReverseProxy = useReverseProxy;
            EnableHttps = enableHttps;
        }

        public bool Enabled { get; }
        public int Port { get; }
        public string ListenAddress { get; }
        public bool UseReverseProxy { get; }
        public bool EnableHttps { get; }
        public string Protocol => EnableHttps && !UseReverseProxy ? "https" : "http";

        public static ExternalWebServerBinding From(PluginConfiguration configuration)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            return new ExternalWebServerBinding(
                configuration.EnableExternalWebServer,
                configuration.ExternalWebServerPort,
                string.IsNullOrWhiteSpace(configuration.ListenAddress)
                    ? "127.0.0.1"
                    : configuration.ListenAddress.Trim(),
                configuration.UseReverseProxy,
                configuration.EnableHttps);
        }

        public bool Equals(ExternalWebServerBinding other)
        {
            return other != null
                && Enabled == other.Enabled
                && Port == other.Port
                && string.Equals(ListenAddress, other.ListenAddress, StringComparison.OrdinalIgnoreCase)
                && UseReverseProxy == other.UseReverseProxy
                && EnableHttps == other.EnableHttps;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as ExternalWebServerBinding);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = Enabled ? 1 : 0;
                hash = (hash * 397) ^ Port;
                hash = (hash * 397) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(ListenAddress);
                hash = (hash * 397) ^ (UseReverseProxy ? 1 : 0);
                hash = (hash * 397) ^ (EnableHttps ? 1 : 0);
                return hash;
            }
        }
    }
}
