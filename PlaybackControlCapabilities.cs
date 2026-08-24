using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Session;

namespace WatchPartyForEmby
{
    /// <summary>
    /// Decides whether a session can receive playback commands. A command is only
    /// considered deliverable when the session advertises both remote-control support
    /// and video playback capability. This avoids treating a server-accepted command as
    /// proof that a real player could execute it.
    /// </summary>
    public static class PlaybackControlCapabilities
    {
        public static bool SessionSupportsRemoteControl(SessionInfo session)
        {
            if (session == null)
            {
                return false;
            }

            try
            {
                return session.SupportsRemoteControl;
            }
            catch (NullReferenceException)
            {
                // Some retained Emby sessions have no ClientCapabilities object.
                // Treat the explicit capability flag as the safe fallback instead
                // of allowing a status/launch request to fail with NullReference.
                return session.Capabilities?.SupportsMediaControl == true;
            }
        }

        public static bool CanReceivePlaybackCommand(
            bool supportsRemoteControl,
            IEnumerable<string> playableMediaTypes)
        {
            if (!supportsRemoteControl || playableMediaTypes == null)
            {
                return false;
            }

            foreach (var mediaType in playableMediaTypes)
            {
                if (string.Equals(mediaType, "Video", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
