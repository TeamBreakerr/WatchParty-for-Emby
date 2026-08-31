using System.Collections.Generic;
using System.Linq;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Selects the concrete media-source identifier carried by PlayNow. Series queue
    /// entries may omit it so Emby can resolve each later episode through its native
    /// playback-info path; they must never inherit the first episode's source or a
    /// stale STRM source.
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

            // Absence or staleness is not a request to choose the first raw media
            // source. In particular, pinning a virtual STRM source here can change the
            // downstream Web URL from Xiaoya's resolved container to stream.strm.
            // Omitting the identifier preserves vanilla Emby/Xiaoya resolution.
            return null;
        }
    }
}
