using System.Collections.Generic;
using System.Linq;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Selects the concrete media-source identifier carried by PlayNow. Series queue
    /// entries may omit it so each later episode can resolve its own default source;
    /// they must never inherit the first episode's source or a prior STRM source.
    /// </summary>
    public static class PlaybackMediaSourceSelector
    {
        public static string Resolve(
            string configuredMediaSourceId,
            IEnumerable<string> availableMediaSourceIds)
        {
            var available = (availableMediaSourceIds ?? Enumerable.Empty<string>())
                .Where(mediaSourceId => !string.IsNullOrWhiteSpace(mediaSourceId))
                .ToList();
            if (!string.IsNullOrWhiteSpace(configuredMediaSourceId))
            {
                var configured = available.FirstOrDefault(mediaSourceId =>
                    string.Equals(
                        mediaSourceId,
                        configuredMediaSourceId,
                        System.StringComparison.Ordinal));
                if (configured != null)
                {
                    return configured;
                }
            }

            return available.FirstOrDefault();
        }
    }
}
