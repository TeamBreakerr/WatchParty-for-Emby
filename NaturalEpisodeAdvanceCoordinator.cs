using System;
using System.Collections.Generic;

namespace WatchPartyForEmby
{
    public enum StoppedPlaybackLifecycleResolution
    {
        Ignored,
        RetainedParticipant,
        RemovedMaster,
        PromotedReplacementMaster
    }

    public enum NaturalEpisodePlaybackStartDisposition
    {
        None,
        ConfirmCommandedSession,
        RejectDifferentSession
    }

    public sealed class NaturalEpisodeCompletion
    {
        public string PartyId { get; set; }
        public string SessionId { get; set; }
        public string PlaySessionId { get; set; }
        public string CompletedEpisodeId { get; set; }
        public string CurrentEpisodeId { get; set; }
        public string NextEpisodeId { get; set; }
        public bool PlayedToCompletion { get; set; }
        public long PositionTicks { get; set; }
        public long RuntimeTicks { get; set; }
    }

    public sealed class NaturalEpisodeAdvanceAuthorization
    {
        internal long Revision { get; set; }
        internal int AttemptCount { get; set; }
        internal DateTime LastAttemptAtUtc { get; set; }

        public string PartyId { get; set; }
        public string SessionId { get; set; }
        public string StoppedPlaySessionId { get; set; }
        public string CompletedEpisodeId { get; set; }
        public string NextEpisodeId { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
    }

    public sealed class NaturalEpisodeAdvanceBeginResult
    {
        public bool StopResolutionAttempted { get; set; }
        public StoppedPlaybackLifecycleResolution StopResolution { get; set; }
        public NaturalEpisodeAdvanceAuthorization Authorization { get; set; }
    }

    public sealed class NaturalEpisodePlaybackStartMatch
    {
        public NaturalEpisodePlaybackStartDisposition Disposition { get; set; }
        public NaturalEpisodeAdvanceAuthorization Authorization { get; set; }
    }

    /// <summary>
    /// Owns the authorization that bridges an exact natural master Stop to the
    /// replacement episode's PlaybackStart. The caller invokes TryBegin inside the
    /// per-party master lifecycle boundary, so cleanup and authorization are atomic.
    /// </summary>
    public sealed class NaturalEpisodeAdvanceCoordinator
    {
        private static readonly long NaturalCompletionWindowTicks =
            TimeSpan.FromSeconds(30).Ticks;
        private readonly object _syncRoot = new object();
        private readonly Dictionary<string, NaturalEpisodeAdvanceAuthorization> _pending =
            new Dictionary<string, NaturalEpisodeAdvanceAuthorization>(StringComparer.Ordinal);
        private long _nextRevision;

        public NaturalEpisodeAdvanceBeginResult TryBegin(
            NaturalEpisodeCompletion completion,
            DateTime nowUtc,
            TimeSpan confirmationWindow,
            Func<StoppedPlaybackLifecycleResolution> resolveStoppedPlayback)
        {
            if (completion == null)
            {
                throw new ArgumentNullException(nameof(completion));
            }
            if (confirmationWindow <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(confirmationWindow));
            }
            if (resolveStoppedPlayback == null)
            {
                throw new ArgumentNullException(nameof(resolveStoppedPlayback));
            }

            var result = new NaturalEpisodeAdvanceBeginResult();
            if (!IsNaturalCompletion(completion))
            {
                return result;
            }

            result.StopResolutionAttempted = true;
            result.StopResolution = resolveStoppedPlayback();
            if (result.StopResolution != StoppedPlaybackLifecycleResolution.RemovedMaster)
            {
                return result;
            }

            lock (_syncRoot)
            {
                var authorization = new NaturalEpisodeAdvanceAuthorization
                {
                    Revision = ++_nextRevision,
                    PartyId = completion.PartyId,
                    SessionId = completion.SessionId,
                    StoppedPlaySessionId = completion.PlaySessionId,
                    CompletedEpisodeId = completion.CompletedEpisodeId,
                    NextEpisodeId = completion.NextEpisodeId,
                    ExpiresAtUtc = nowUtc.Add(confirmationWindow),
                    LastAttemptAtUtc = DateTime.MinValue
                };
                _pending[completion.PartyId] = authorization;
                result.Authorization = Clone(authorization);
            }

            return result;
        }

        public NaturalEpisodePlaybackStartMatch ClassifyPlaybackStart(
            string partyId,
            string sessionId,
            string episodeItemId,
            DateTime nowUtc)
        {
            lock (_syncRoot)
            {
                if (!TryGetCurrent(partyId, nowUtc, out var authorization)
                    || !string.Equals(
                        authorization.NextEpisodeId,
                        episodeItemId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return new NaturalEpisodePlaybackStartMatch
                    {
                        Disposition = NaturalEpisodePlaybackStartDisposition.None
                    };
                }

                return new NaturalEpisodePlaybackStartMatch
                {
                    Disposition = string.Equals(
                        authorization.SessionId,
                        sessionId,
                        StringComparison.Ordinal)
                            ? NaturalEpisodePlaybackStartDisposition.ConfirmCommandedSession
                            : NaturalEpisodePlaybackStartDisposition.RejectDifferentSession,
                    Authorization = Clone(authorization)
                };
            }
        }

        public bool IsCurrent(
            NaturalEpisodeAdvanceAuthorization authorization,
            DateTime nowUtc)
        {
            if (authorization == null)
            {
                return false;
            }

            lock (_syncRoot)
            {
                return TryGetCurrent(authorization.PartyId, nowUtc, out var current)
                    && Matches(current, authorization);
            }
        }

        public bool TryComplete(
            NaturalEpisodeAdvanceAuthorization authorization,
            string sessionId,
            string episodeItemId,
            DateTime nowUtc,
            Func<bool> commit)
        {
            if (authorization == null)
            {
                return false;
            }
            if (commit == null)
            {
                throw new ArgumentNullException(nameof(commit));
            }

            lock (_syncRoot)
            {
                if (!TryGetCurrent(authorization.PartyId, nowUtc, out var current)
                    || !Matches(current, authorization)
                    || !string.Equals(
                        current.SessionId,
                        sessionId,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        current.NextEpisodeId,
                        episodeItemId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (!commit())
                {
                    return false;
                }

                return _pending.Remove(current.PartyId);
            }
        }

        public bool CancelParty(string partyId)
        {
            lock (_syncRoot)
            {
                return !string.IsNullOrEmpty(partyId) && _pending.Remove(partyId);
            }
        }

        public bool TryBeginCommandAttempt(
            NaturalEpisodeAdvanceAuthorization authorization,
            DateTime nowUtc,
            TimeSpan retryInterval,
            int maxAttempts,
            out int attempt)
        {
            attempt = 0;
            if (authorization == null)
            {
                return false;
            }
            if (retryInterval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(retryInterval));
            }
            if (maxAttempts <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxAttempts));
            }

            lock (_syncRoot)
            {
                if (!TryGetCurrent(authorization.PartyId, nowUtc, out var current)
                    || !Matches(current, authorization))
                {
                    return false;
                }

                attempt = current.AttemptCount;
                if (current.AttemptCount >= maxAttempts
                    || (current.AttemptCount > 0
                        && nowUtc - current.LastAttemptAtUtc < retryInterval))
                {
                    return false;
                }

                current.AttemptCount++;
                current.LastAttemptAtUtc = nowUtc;
                attempt = current.AttemptCount;
                return true;
            }
        }

        private bool TryGetCurrent(
            string partyId,
            DateTime nowUtc,
            out NaturalEpisodeAdvanceAuthorization authorization)
        {
            authorization = null;
            if (string.IsNullOrEmpty(partyId)
                || !_pending.TryGetValue(partyId, out var current))
            {
                return false;
            }

            if (current.ExpiresAtUtc < nowUtc)
            {
                _pending.Remove(partyId);
                return false;
            }

            authorization = current;
            return true;
        }

        private static bool IsNaturalCompletion(NaturalEpisodeCompletion completion)
        {
            if (string.IsNullOrEmpty(completion.PartyId)
                || string.IsNullOrEmpty(completion.SessionId)
                || string.IsNullOrEmpty(completion.PlaySessionId)
                || string.IsNullOrEmpty(completion.CompletedEpisodeId)
                || string.IsNullOrEmpty(completion.CurrentEpisodeId)
                || string.IsNullOrEmpty(completion.NextEpisodeId)
                || !completion.PlayedToCompletion
                || completion.RuntimeTicks <= 0
                || completion.PositionTicks < 0
                || !string.Equals(
                    completion.CompletedEpisodeId,
                    completion.CurrentEpisodeId,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    completion.CompletedEpisodeId,
                    completion.NextEpisodeId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var completionThreshold = Math.Max(
                completion.RuntimeTicks - NaturalCompletionWindowTicks,
                (long)Math.Floor(completion.RuntimeTicks * 0.95));
            return completion.PositionTicks >= Math.Max(0, completionThreshold);
        }

        private static NaturalEpisodeAdvanceAuthorization Clone(
            NaturalEpisodeAdvanceAuthorization authorization)
        {
            return authorization == null
                ? null
                : new NaturalEpisodeAdvanceAuthorization
                {
                    Revision = authorization.Revision,
                    PartyId = authorization.PartyId,
                    SessionId = authorization.SessionId,
                    StoppedPlaySessionId = authorization.StoppedPlaySessionId,
                    CompletedEpisodeId = authorization.CompletedEpisodeId,
                    NextEpisodeId = authorization.NextEpisodeId,
                    ExpiresAtUtc = authorization.ExpiresAtUtc,
                    AttemptCount = authorization.AttemptCount,
                    LastAttemptAtUtc = authorization.LastAttemptAtUtc
                };
        }

        private static bool Matches(
            NaturalEpisodeAdvanceAuthorization current,
            NaturalEpisodeAdvanceAuthorization candidate)
        {
            return current != null
                && candidate != null
                && current.Revision == candidate.Revision
                && string.Equals(
                    current.PartyId,
                    candidate.PartyId,
                    StringComparison.Ordinal)
                && string.Equals(
                    current.SessionId,
                    candidate.SessionId,
                    StringComparison.Ordinal)
                && string.Equals(
                    current.StoppedPlaySessionId,
                    candidate.StoppedPlaySessionId,
                    StringComparison.Ordinal)
                && string.Equals(
                    current.CompletedEpisodeId,
                    candidate.CompletedEpisodeId,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    current.NextEpisodeId,
                    candidate.NextEpisodeId,
                    StringComparison.OrdinalIgnoreCase);
        }
    }
}
