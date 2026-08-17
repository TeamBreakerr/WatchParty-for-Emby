using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Session;

namespace WatchPartyForEmby
{
    public class ServerEntryPoint : IServerEntryPoint
    {
        private readonly ISessionManager _sessionManager;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;
        private readonly Plugin _plugin;
        private Timer _syncTimer;
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _partySyncedSessions = new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>();
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, bool>> _partySessionPauseState = new ConcurrentDictionary<string, ConcurrentDictionary<string, bool>>();
        private readonly HashSet<string> _trackedPartyIds = new HashSet<string>();
        private readonly HashSet<string> _trackedActivePartyIds = new HashSet<string>();
        private readonly PauseControlCoordinator _pauseControlCoordinator =
            new PauseControlCoordinator();
        private readonly HashSet<string> _partiesTransitioning = new HashSet<string>();
        private readonly SeriesSelectionCoordinator _seriesSelections =
            new SeriesSelectionCoordinator();
        private readonly SeriesEpisodeTransitionTracker _seriesEpisodeTransitions =
            new SeriesEpisodeTransitionTracker();
        private readonly object _seriesTransitionLock = new object();
        private readonly object _configurationMaintenanceLock = new object();
        private readonly ConcurrentDictionary<string, DateTime> _lastProgressCheckpoint = new ConcurrentDictionary<string, DateTime>();
        private readonly PlaybackSyncCoordinator _playbackSyncCoordinator = new PlaybackSyncCoordinator();
        private readonly CancellationTokenSource _lifetimeCts = new CancellationTokenSource();
        private readonly KeyedAsyncSerialQueue<string> _playbackEventQueue =
            new KeyedAsyncSerialQueue<string>(capacityPerKey: 128);
        private IDisposable _waitingRoomStartRegistration;
        private int _syncPassRunning;
        private static readonly TimeSpan ProgressCheckpointInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan PartyValidationInterval = TimeSpan.FromMinutes(5);
        // Clients normally report progress every 5-10 seconds. Periodic calibration
        // projects a cached participant position for at most this long before comparing
        // it with the master, avoiding false drift without hiding a real seek.
        private static readonly TimeSpan MaxParticipantPositionProjection = TimeSpan.FromSeconds(15);
        // When the master drags the timeline, Emby Web emits a position-less "unknown"
        // report, then a real ~1s position while the video element is recreated, then the
        // real target up to ~9s later. Coalesce the burst into a single seek per drag so
        // the participant is not commanded to the transient near-zero position.
        private static readonly TimeSpan MasterSeekDebounce = TimeSpan.FromMilliseconds(1500);
        private static readonly TimeSpan MasterSeekNearZeroThreshold = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan MasterSeekDrasticFromThreshold = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan MasterSeekDrasticDebounce = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan SeriesSelectionCoalesceWindow =
            TimeSpan.FromSeconds(1);
        private DateTime _lastPartyValidationUtc = DateTime.MinValue;
        private readonly object _masterSeekSyncLock = new object();
        private readonly Dictionary<string, PendingMasterSeekSync> _pendingMasterSeeks =
            new Dictionary<string, PendingMasterSeekSync>();

        public ServerEntryPoint(
            ISessionManager sessionManager,
            ILibraryManager libraryManager,
            ILogManager logManager)
        {
            _sessionManager = sessionManager;
            _libraryManager = libraryManager;
            _logger = logManager.GetLogger(GetType().Name);
            _plugin = Plugin.Instance;
        }

        public void Run()
        {
            _logger.Info("Watch Party plugin started");

            _waitingRoomStartRegistration = _plugin.WaitingRoomStarts.Register(
                StartPartyFromWaitingRoom);

            _sessionManager.PlaybackStart += OnPlaybackStart;
            _sessionManager.PlaybackProgress += OnPlaybackProgress;
            _sessionManager.PlaybackStopped += OnPlaybackStopped;

            _plugin.ConfigurationUpdated += OnConfigurationUpdated;

            var requiresDirectItemUpgrade = _plugin.Configuration.ConfigurationVersion
                < PluginConfigurationMigration.DirectItemBindingVersion;
            var migratedRoomCount = PluginConfigurationMigration.UpgradeLegacyRooms(_plugin.Configuration);
            if (requiresDirectItemUpgrade)
            {
                _plugin.SaveConfigurationSafely();
                _logger.Info(
                    $"Direct-item configuration upgrade preserved {migratedRoomCount} existing room(s)");
            }

            NormalizePartyConfiguration();

            var config = _plugin.Configuration;
            foreach (var party in config.WatchParties)
            {
                _trackedPartyIds.Add(party.Id);
                if (party.IsActive)
                {
                    _trackedActivePartyIds.Add(party.Id);
                }
                _pauseControlCoordinator.ResetParty(
                    party.Id,
                    isPaused: !party.IsPlaying);
            }

            var intervalMs = Math.Max(1, config.SyncIntervalSeconds) * 1000;
            // Do not validate every episode in every persisted Series Party during
            // server/plugin startup. Large queues can contain thousands of items and
            // compete with the first Emby client page load. Periodic maintenance starts
            // after the normal validation interval; an explicit configuration update
            // still requests immediate validation.
            _lastPartyValidationUtc = DateTime.UtcNow;
            _syncTimer = new Timer(CheckAndSyncUsers, null, intervalMs, intervalMs);
        }

        private void NormalizePartyConfiguration()
        {
            lock (_plugin.ConfigurationSyncRoot)
            {
                var changed = false;
                foreach (var party in _plugin.Configuration.WatchParties)
                {
                    changed |= WatchPartyConfigurationPolicy.Normalize(party);
                }

                if (changed)
                {
                    _plugin.SaveConfigurationSafely();
                    _logger.Info("Normalized persisted Watch Party configuration");
                }
            }
        }

        private void OnConfigurationUpdated(object sender, EventArgs e)
        {
            _logger.Info("Configuration updated, refreshing collections and timer");

            NormalizePartyConfiguration();
            var config = _plugin.Configuration;
            foreach (var party in config.WatchParties)
            {
                _pauseControlCoordinator.ResetParty(
                    party.Id,
                    isPaused: !party.IsPlaying);
            }
            var intervalMs = Math.Max(1, config.SyncIntervalSeconds) * 1000;
            _syncTimer?.Change(intervalMs, intervalMs);

            Task.Run(
                () =>
                {
                    CleanupRemovedParties();
                    CleanupIneligibleParticipants();
                    ValidateAndCleanWatchParties(force: true);
                },
                _lifetimeCts.Token);
        }

        private PartyParticipant GetOrCreateParticipant(
            string partyId,
            SessionInfo session,
            string playSessionId = null,
            DateTime? nowUtc = null,
            int? maxParticipants = null)
        {
            var user = session.UserName ?? session.UserId;
            if (!maxParticipants.HasValue)
            {
                lock (_plugin.ConfigurationSyncRoot)
                {
                    maxParticipants = _plugin.Configuration.WatchParties
                        .FirstOrDefault(party => party.Id == partyId)
                        ?.MaxParticipants ?? 0;
                }
            }

            if (!_plugin.PartyParticipants.TryUpsertSession(
                    partyId,
                    session.Id,
                    session.UserId,
                    user,
                    playSessionId,
                    nowUtc ?? DateTime.UtcNow,
                    maxParticipants.Value,
                    out var participant,
                    out var previousPlaySessionId,
                    out var created))
            {
                _logger.Warn(
                    $"[Party {partyId}] Refused session {session.Id} for user {session.UserId}: " +
                    $"maximum distinct-user capacity ({maxParticipants.Value}) reached");
                return null;
            }
            if (created)
            {
                _logger.Info($"[Party {partyId}] New participant session: {user} ({session.UserId}) session={session.Id}");
            }

            if (_playbackSyncCoordinator.ResetForNewPlayback(
                    session.Id,
                    previousPlaySessionId,
                    playSessionId))
            {
                ClearSessionEpisodeMarkers(partyId, session.Id);
                _logger.Debug(
                    $"[Party {partyId}] Cleared prior command state for new playback " +
                    $"{playSessionId} on session {session.Id}");
            }

            return participant;
        }

        private void ClearSessionEpisodeMarkers(string partyId, string sessionId)
        {
            if (_partySyncedSessions.TryGetValue(partyId, out var syncedSessions))
            {
                syncedSessions.TryRemove(sessionId, out _);
            }
            if (_partySessionPauseState.TryGetValue(partyId, out var pauseStates))
            {
                pauseStates.TryRemove(sessionId, out _);
            }
        }

        private void UpdateParticipantActivity(string partyId, string sessionId, long positionTicks, bool isPaused)
        {
            _plugin.PartyParticipants.UpdateActivity(
                partyId,
                sessionId,
                positionTicks,
                isPaused,
                DateTime.UtcNow);
        }

        private bool TryRemoveParticipant(
            string partyId,
            string sessionId,
            out bool wasMaster,
            string expectedPlaySessionId = null,
            bool requirePlaySessionMatch = false)
        {
            PartyParticipant participant;
            var removed = requirePlaySessionMatch
                ? _plugin.PartyParticipants.TryRemoveSession(
                    partyId,
                    sessionId,
                    expectedPlaySessionId,
                    out participant,
                    out wasMaster)
                : _plugin.PartyParticipants.TryRemoveSession(
                    partyId,
                    sessionId,
                    out participant,
                    out wasMaster);
            if (removed)
            {
                CompleteParticipantRemoval(partyId, sessionId, participant);
                return true;
            }

            wasMaster = false;
            return false;
        }

        private bool TryRemoveInactiveParticipant(
            string partyId,
            string sessionId,
            DateTime inactiveBeforeUtc,
            out bool wasMaster)
        {
            if (!_plugin.PartyParticipants.TryRemoveSessionIfInactive(
                    partyId,
                    sessionId,
                    inactiveBeforeUtc,
                    out var participant,
                    out wasMaster))
            {
                return false;
            }

            CompleteParticipantRemoval(partyId, sessionId, participant);
            return true;
        }

        private void CompleteParticipantRemoval(
            string partyId,
            string sessionId,
            PartyParticipant participant)
        {
            _playbackSyncCoordinator.ClearSession(sessionId);
            _seriesEpisodeTransitions.ClearSession(sessionId);
            ClearSessionEpisodeMarkers(partyId, sessionId);
            if (!_plugin.PartyParticipants.HasUser(partyId, participant.UserId))
            {
                _plugin.PartyReadyUsers.RemoveUser(partyId, participant.UserId);
                _pauseControlCoordinator.RemoveMember(partyId, participant.UserId);
            }
            _logger.Info(
                $"[Party {partyId}] Participant session left: {participant.UserName} ({sessionId})");
        }

        private bool IsMasterSession(WatchPartyItem party, SessionInfo session)
        {
            return party != null
                && session != null
                && _plugin.PartyParticipants.IsMasterSession(party.Id, session.Id);
        }

        /// <summary>
        /// Resolves whether this session is the party master. When the configured master
        /// user starts a session before any master session is registered, the first such
        /// session becomes the master; later sessions of the same user never overwrite it.
        /// </summary>
        private bool TryResolveMasterSession(WatchPartyItem party, SessionInfo session)
        {
            if (IsMasterSession(party, session))
            {
                return true;
            }

            if (party == null
                || session == null
                || string.IsNullOrEmpty(party.MasterUserId)
                || !string.Equals(
                    session.UserId,
                    party.MasterUserId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (GetOrCreateParticipant(
                    party.Id,
                    session,
                    maxParticipants: party.MaxParticipants) == null)
            {
                return false;
            }

            var registeredMasterSessionId = _plugin.PartyParticipants.GetMasterSession(party.Id);
            if (!string.IsNullOrEmpty(registeredMasterSessionId)
                && !string.Equals(registeredMasterSessionId, session.Id, StringComparison.Ordinal))
            {
                // If the registered master session is no longer actively playing
                // (e.g. the client quit without a clean stop event), let the new
                // session of the master user take over instead of being treated
                // as a regular participant.
                if (!MasterSessionSelectionPolicy.CanTakeOver(
                        registeredMasterSessionId,
                        session.Id,
                        candidateSessionId => _sessionManager.Sessions.Any(candidate =>
                            string.Equals(candidate.Id, candidateSessionId, StringComparison.Ordinal)
                            && IsCurrentPartyPlayback(party, candidate))))
                {
                    return false;
                }

                _plugin.PartyParticipants.SetMasterSession(party.Id, session.Id);
                _logger.Info($"[Watch Party] Promoted session {session.Id} as master; previous master session {registeredMasterSessionId} is no longer active");
                return true;
            }

            return _plugin.PartyParticipants.SetMasterSessionIfAbsent(party.Id, session.Id);
        }

        /// <summary>
        /// A party whose configured master user is not currently playing has no live
        /// clock to follow. Syncing participants in that state would drag everyone back
        /// to a stale frozen position, so callers must skip position correction until
        /// the master session is actually active again.
        /// </summary>
        private bool HasActiveMasterSession(WatchPartyItem party)
        {
            if (party == null)
            {
                return false;
            }

            if (string.IsNullOrEmpty(party.MasterUserId))
            {
                return true;
            }

            return GetActiveMasterSession(party) != null;
        }

    private SessionInfo GetActiveMasterSession(WatchPartyItem party)
        {
            if (party == null)
            {
                return null;
            }

            var masterSessionId = _plugin.PartyParticipants.GetMasterSession(party.Id);
            return string.IsNullOrEmpty(masterSessionId)
                ? null
                : _sessionManager.Sessions.FirstOrDefault(s =>
                    string.Equals(s.Id, masterSessionId, StringComparison.Ordinal)
                    && IsCurrentPartyPlayback(party, s));
        }

        private bool IsCurrentPartyPlayback(WatchPartyItem party, SessionInfo session)
        {
            if (party == null || session?.NowPlayingItem == null)
            {
                return false;
            }

            var item = _libraryManager.GetItemById(session.NowPlayingItem.Id);
            var matchingParty = FindPartyForItem(item);
            if (matchingParty == null || !string.Equals(matchingParty.Id, party.Id, StringComparison.Ordinal))
            {
                return false;
            }

            if (!party.IsSeriesParty)
            {
                return true;
            }

            var currentEpisode = SeriesPartyQueue.GetCurrentEpisode(party);
            var episodeId = FindSeriesEpisodeId(party, item);
            return currentEpisode != null
                && !string.IsNullOrEmpty(episodeId)
                && string.Equals(episodeId, currentEpisode.ItemId, StringComparison.OrdinalIgnoreCase);
        }

        private bool TryPromoteActiveMasterSession(
            WatchPartyItem party,
            string userId,
            out string promotedSessionId)
        {
            promotedSessionId = null;
            if (party == null || string.IsNullOrWhiteSpace(userId))
            {
                return false;
            }

            var candidateSessionId = MasterSessionSelectionPolicy.SelectLatestActiveSession(
                _plugin.PartyParticipants.GetSessions(party.Id),
                userId,
                sessionId => _sessionManager.Sessions.Any(current =>
                    string.Equals(current.Id, sessionId, StringComparison.Ordinal)
                    && IsCurrentPartyPlayback(party, current)));
            if (!string.IsNullOrEmpty(candidateSessionId)
                && _plugin.PartyParticipants.SetMasterSession(party.Id, candidateSessionId))
            {
                promotedSessionId = candidateSessionId;
                return true;
            }

            return false;
        }

        private bool CanUserJoinParty(WatchPartyItem party, string userId)
        {
            if (party.AllowedUserIds != null && party.AllowedUserIds.Count > 0)
            {
                if (!party.AllowedUserIds.Any(allowedUserId =>
                        string.Equals(
                            allowedUserId,
                            userId,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.Warn($"[Party {party.Id}] User {userId} not in allowed list");
                    return false;
                }
            }

            if (_plugin.PartyParticipants.HasUser(party.Id, userId))
            {
                return true;
            }

            if (party.MaxParticipants > 0
                && _plugin.PartyParticipants.DistinctUserCount(party.Id) >= party.MaxParticipants)
            {
                _logger.Warn($"[Party {party.Id}] Maximum participants ({party.MaxParticipants}) reached");
                return false;
            }

            return true;
        }

    private void CheckAndRemoveInactiveParticipants(WatchPartyItem party)
        {
            if (!party.AutoKickInactiveMinutes
                || _plugin.PartyParticipants.SessionCount(party.Id) == 0)
            {
                return;
            }

            var inactiveThreshold = DateTime.UtcNow.AddMinutes(-party.InactiveTimeoutMinutes);
            var inactiveSessions = _plugin.PartyParticipants.GetSessions(party.Id)
                .Where(participant => participant.LastActivityAt < inactiveThreshold)
                .ToList();

            foreach (var participant in inactiveSessions)
            {
                _logger.Info($"[Party {party.Id}] Removing inactive session: {participant.UserName} ({participant.SessionId}) (inactive for {party.InactiveTimeoutMinutes} minutes)");
                if (!TryRemoveInactiveParticipant(
                        party.Id,
                        participant.SessionId,
                        inactiveThreshold,
                        out var removedMaster))
                {
                    continue;
                }

                if (removedMaster)
                {
                    var promoted = HandleMasterDeparture(
                        party,
                        participant.UserId,
                        party.CurrentPositionTicks,
                        "inactive timeout");
                    if (!promoted)
                    {
                        _plugin.SaveConfigurationSafely();
                    }
                }

            }
        }

        private bool HandleMasterDeparture(
            WatchPartyItem party,
            string userId,
            long fallbackPositionTicks,
            string reason,
            bool freezeAtFallbackPosition = false)
        {
            if (TryPromoteActiveMasterSession(party, userId, out var promotedSessionId))
            {
                _logger.Info(
                    $"[Party {party.Id}] Promoted session {promotedSessionId} as master after {reason}");
                CancelPendingMasterSeekSync(party.Id);
                return true;
            }

            var nowUtc = DateTime.UtcNow;
            var frozenPosition = freezeAtFallbackPosition
                ? Math.Max(0, fallbackPositionTicks)
                : _playbackSyncCoordinator.GetEstimatedPartyPosition(
                    party.Id,
                    fallbackPositionTicks,
                    nowUtc);
            party.CurrentPositionTicks = frozenPosition;
            party.IsPlaying = false;
            _playbackSyncCoordinator.StopMasterClock(party.Id, frozenPosition, nowUtc);
            _pauseControlCoordinator.ObserveAuthoritativeState(party.Id, isPaused: true);
            CancelPendingMasterSeekSync(party.Id);
            if (!party.IsWaitingRoom && party.MinReadyCount > 1)
            {
                party.IsWaitingRoom = true;
                _logger.Info(
                    $"[Party {party.Id}] Re-enabled waiting room after master departure " +
                    $"(MinReadyCount={party.MinReadyCount})");
            }

            if (_partySyncedSessions.TryGetValue(party.Id, out var syncedSessions))
            {
                syncedSessions.Clear();
            }
            if (_partySessionPauseState.TryGetValue(party.Id, out var pauseStates))
            {
                pauseStates.Clear();
            }
            _plugin.PartyReadyUsers.ClearParty(party.Id);
            _logger.Info(
                $"[Party {party.Id}] Master user {userId} left ({reason}); " +
                "stopped the authoritative clock");
            return false;
        }

        private async Task CheckWaitingRoomReadiness(WatchPartyItem party)
        {
            if (!party.IsWaitingRoom || party.IsPlaying)
            {
                _logger.Debug($"[Party {party.Id}] Skipping waiting room check - IsWaitingRoom: {party.IsWaitingRoom}, IsPlaying: {party.IsPlaying}");
                return;
            }

            var presentUserIds = _plugin.PartyParticipants.GetSessions(party.Id)
                .Select(participant => participant.UserId)
                .Where(userId => !string.IsNullOrWhiteSpace(userId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var readyCount = WaitingRoomPolicy.CountPresentReadyUsers(
                _plugin.PartyReadyUsers.GetReadyUsersSnapshot(party.Id),
                presentUserIds);
            var participantCount = presentUserIds.Count;
            _logger.Info(
                $"[Party {party.Id}] Waiting room: {readyCount} ready, " +
                $"{participantCount} present, minimum {party.MinReadyCount}");

            if (WaitingRoomPolicy.ShouldAutoStart(party, readyCount))
            {
                _logger.Info(
                    $"[Party {party.Id}] Minimum ready count reached; starting party");
                await _plugin.WaitingRoomStarts.StartAsync(party.Id);
            }
            else
            {
                _logger.Debug(
                    $"[Party {party.Id}] Auto-start condition not met");
            }
        }

        private async Task<bool> StartPartyFromWaitingRoom(string partyId)
        {
            _lifetimeCts.Token.ThrowIfCancellationRequested();

            WatchPartyItem party;
            lock (_plugin.ConfigurationSyncRoot)
            {
                _lifetimeCts.Token.ThrowIfCancellationRequested();
                party = _plugin.Configuration.WatchParties.FirstOrDefault(
                    candidate => candidate.Id == partyId);
                if (party == null
                    || !party.IsActive
                    || !party.IsWaitingRoom
                    || party.IsPlaying)
                {
                    _logger.Debug(
                        $"[Party {partyId}] Start ignored because the room is missing, " +
                        "inactive, or no longer waiting");
                    return false;
                }

                party.IsWaitingRoom = false;
                party.IsPlaying = true;
                var nowUtc = DateTime.UtcNow;
                try
                {
                    _playbackSyncCoordinator.UpdateMasterPosition(
                        party.Id,
                        party.CurrentPositionTicks,
                        true,
                        nowUtc,
                        TimeSpan.FromSeconds(party.SyncToleranceSeconds).Ticks);
                    _plugin.SaveConfigurationSafely();
                    _pauseControlCoordinator.ObserveAuthoritativeState(
                        party.Id,
                        isPaused: false);
                }
                catch
                {
                    party.IsWaitingRoom = true;
                    party.IsPlaying = false;
                    _playbackSyncCoordinator.StopMasterClock(
                        party.Id,
                        party.CurrentPositionTicks,
                        nowUtc);
                    throw;
                }
            }

            var sessions = _sessionManager.Sessions.Where(s => s.NowPlayingItem != null).ToList();
            foreach (var session in sessions)
            {
                if (!_plugin.PartyParticipants.TryGetSession(party.Id, session.Id, out _)
                    || !SupportsRemoteControlledPlayback(session))
                {
                    continue;
                }

                var item = _libraryManager.GetItemById(session.NowPlayingItem.Id);
                var matchingParty = FindPartyForItem(item);
                if (matchingParty == null || matchingParty.Id != party.Id)
                {
                    continue;
                }

                try
                {
                    if (await SendPauseStateCommand(session, false))
                    {
                        _logger.Info($"[Party {party.Id}] Started playback for {session.UserName}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.ErrorException($"[Party {party.Id}] Error starting playback for {session.UserName}", ex);
                }
            }

            return true;
        }

        private async Task HandlePauseAttempt(
            WatchPartyItem party,
            SessionInfo session,
            bool isMaster,
            long? reportedPositionTicks)
        {
            var decision = EvaluatePauseControl(
                party,
                session,
                isMaster,
                requestedIsPaused: true);
            if (decision.Kind == PauseControlDecisionKind.Broadcast)
            {
                _logger.Info($"[Party {party.Id}] {session.UserName} paused; broadcasting authoritative pause");
                await PauseAllUsers(party, session.Id);
                await SyncParticipantsAfterPause(
                    party,
                    session,
                    isMaster,
                    reportedPositionTicks);
                return;
            }

            LogPendingOrRejectedPauseDecision(party, session, decision);
            await SendPauseStateCommand(session, decision.AuthoritativeIsPaused);
        }

        private async Task SyncParticipantsAfterPause(
            WatchPartyItem party,
            SessionInfo pauseInitiator,
            bool initiatorIsMaster,
            long? reportedPositionTicks)
        {
            // A drag immediately before pause may still have a debounced sync waiting to
            // fire. The explicit pause sync supersedes it and must be the only final seek.
            CancelPendingMasterSeekSync(party.Id);

            var controllingSession = GetActiveMasterSession(party);
            if (controllingSession == null)
            {
                _logger.Info($"[Party {party.Id}] Cannot run pause position sync because no active master session is available");
                return;
            }

            // If the master initiated the pause, its event carries the exact final
            // position. If somebody else paused, the master's projected clock remains
            // authoritative; the participant's own position must never overwrite it.
            var targetPosition = initiatorIsMaster && reportedPositionTicks.HasValue
                ? Math.Max(0, reportedPositionTicks.Value)
                : _playbackSyncCoordinator.GetEstimatedPartyPosition(
                    party.Id,
                    party.CurrentPositionTicks,
                    DateTime.UtcNow);

            _logger.Info(
                $"[Party {party.Id}] Pause accepted from {pauseInitiator.UserName}; " +
                $"actively syncing participants to master position " +
                $"{TimeSpan.FromTicks(targetPosition).TotalSeconds:F1}s");

            var sessions = _sessionManager.Sessions.Where(s => s.NowPlayingItem != null).ToList();
            foreach (var session in sessions)
            {
                // The registered master defines the pause position and is never seeked
                // back to a participant's clock.
                if (IsMasterSession(party, session))
                {
                    continue;
                }

                var item = _libraryManager.GetItemById(session.NowPlayingItem.Id);
                var matchingParty = FindPartyForItem(item);
                if (matchingParty == null || matchingParty.Id != party.Id)
                {
                    continue;
                }
                if (!_plugin.PartyParticipants.TryGetSession(
                        party.Id,
                        session.Id,
                        out _))
                {
                    continue;
                }

                await SyncUserToPosition(
                    session,
                    item,
                    targetPosition,
                    allowReplace: true,
                    controllingSession: controllingSession,
                    applySyncOffset: false,
                    force: true);
            }
        }

        private async Task PauseAllUsers(WatchPartyItem party, string excludeSessionId)
        {
            await BroadcastPauseState(party, excludeSessionId, isPaused: true);
        }

        private async Task HandleUnpauseAttempt(WatchPartyItem party, SessionInfo session, bool isHost)
        {
            var decision = EvaluatePauseControl(
                party,
                session,
                isHost,
                requestedIsPaused: false);
            if (decision.Kind == PauseControlDecisionKind.Broadcast)
            {
                _logger.Info($"[Party {party.Id}] {session.UserName} resumed; broadcasting authoritative play state");
                await UnpauseAllUsers(party, session.Id);
                return;
            }

            LogPendingOrRejectedPauseDecision(party, session, decision);
            await SendPauseStateCommand(session, decision.AuthoritativeIsPaused);
        }

        private PauseControlDecision EvaluatePauseControl(
            WatchPartyItem party,
            SessionInfo session,
            bool isMaster,
            bool requestedIsPaused)
        {
            return _pauseControlCoordinator.HandleRequest(
                party.Id,
                PauseControlModeParser.Parse(party.PauseControl),
                session.UserId,
                isMaster,
                requestedIsPaused,
                Math.Max(1, _plugin.PartyParticipants.DistinctUserCount(party.Id)));
        }

        private void LogPendingOrRejectedPauseDecision(
            WatchPartyItem party,
            SessionInfo session,
            PauseControlDecision decision)
        {
            if (decision.Kind == PauseControlDecisionKind.WaitForVotes)
            {
                _logger.Info(
                    $"[Party {party.Id}] Playback-state vote from {session.UserName}: " +
                    $"{decision.VoteCount}/{decision.RequiredVotes}; restoring actor to authoritative state");
                return;
            }

            _logger.Warn(
                $"[Party {party.Id}] Playback-state request from {session.UserName} rejected by {party.PauseControl} policy");
        }

        private async Task UnpauseAllUsers(WatchPartyItem party, string excludeSessionId)
        {
            await BroadcastPauseState(party, excludeSessionId, isPaused: false);
        }

        private async Task BroadcastPauseState(
            WatchPartyItem party,
            string excludeSessionId,
            bool isPaused)
        {
            try
            {
                var sessions = _sessionManager.Sessions.Where(s => s.NowPlayingItem != null).ToList();

                foreach (var otherSession in sessions)
                {
                    if (otherSession.Id == excludeSessionId)
                    {
                        continue;
                    }

                    var item = _libraryManager.GetItemById(otherSession.NowPlayingItem.Id);
                    var matchingParty = FindPartyForItem(item);

                    if (matchingParty != null
                        && matchingParty.Id == party.Id
                        && _plugin.PartyParticipants.TryGetSession(
                            party.Id,
                            otherSession.Id,
                            out _))
                    {
                        var commandName = isPaused ? "Pausing" : "Resuming";
                        _logger.Info(
                            $"[Party {party.Id}] {commandName} user {otherSession.UserName} " +
                            $"(Session: {otherSession.Id})");
                        try
                        {
                            await SendPauseStateCommand(otherSession, isPaused);
                        }
                        catch (Exception ex)
                        {
                            _logger.ErrorException(
                                $"[Party {party.Id}] Error sending {(isPaused ? "pause" : "resume")} " +
                                $"to user {otherSession.UserName}",
                                ex);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException(
                    $"[Party {party.Id}] Error broadcasting {(isPaused ? "pause" : "resume")}",
                    ex);
            }
        }

        private async Task<bool> SendPauseStateCommand(SessionInfo session, bool isPaused)
        {
            if (session == null
                || !SupportsRemoteControlledPlayback(session))
            {
                _logger.Info(
                    $"[Watch Party] Session {session?.Id} ({session?.Client}) does not " +
                    $"advertise pause control, skipping {(isPaused ? "pause" : "unpause")}");
                return false;
            }

            var expectation = _playbackSyncCoordinator.ExpectPauseState(
                session.Id,
                isPaused,
                DateTime.UtcNow);
            try
            {
                await _sessionManager.SendPlaystateCommand(
                    session.Id,
                    session.Id,
                    new PlaystateRequest
                    {
                        Command = isPaused ? PlaystateCommand.Pause : PlaystateCommand.Unpause
                    },
                    _lifetimeCts.Token);
                return true;
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
            {
                _playbackSyncCoordinator.CancelExpectedPauseState(
                    session.Id,
                    expectation);
                return false;
            }
            catch
            {
                _playbackSyncCoordinator.CancelExpectedPauseState(
                    session.Id,
                    expectation);
                throw;
            }
        }

        private void ValidateAndCleanWatchParties(bool force = false)
        {
            lock (_configurationMaintenanceLock)
            {
                lock (_plugin.ConfigurationSyncRoot)
                {
                    ValidateAndCleanWatchPartiesCore(force);
                }
            }
        }

        private void ValidateAndCleanWatchPartiesCore(bool force)
        {
            try
            {
                var nowUtc = DateTime.UtcNow;
                if (!force && nowUtc - _lastPartyValidationUtc < PartyValidationInterval)
                {
                    return;
                }
                _lastPartyValidationUtc = nowUtc;

                var config = _plugin.Configuration;
                bool configChanged = false;

                foreach (var party in config.WatchParties.ToList())
                {
                    if (party.IsSeriesParty)
                    {
                        var originalEpisodeCount = party.EpisodeQueue?.Count ?? 0;
                        var queueChanged = SeriesPartyQueue.RemoveUnavailableEpisodes(
                            party,
                            itemId => _libraryManager.GetItemById(itemId) != null);
                        if (queueChanged)
                        {
                            var remainingEpisodeCount = party.EpisodeQueue?.Count ?? 0;
                            var removedEpisodeCount = originalEpisodeCount - remainingEpisodeCount;
                            if (removedEpisodeCount > 0)
                            {
                                _logger.Info(
                                    $"Party {party.Id}: Removed {removedEpisodeCount} unavailable episode(s)");
                            }
                            configChanged = true;
                        }

                        if (party.EpisodeQueue == null || party.EpisodeQueue.Count == 0)
                        {
                            _logger.Info(
                                $"Party {party.Id}: No source episodes remain for {party.SeriesName}, removing room");
                            config.WatchParties.Remove(party);
                            configChanged = true;
                        }
                    }
                    else if (_libraryManager.GetItemById(party.ItemId) == null)
                    {
                        _logger.Info($"Party {party.Id}: Item {party.ItemName} no longer exists in library, removing from settings");
                        config.WatchParties.Remove(party);
                        configChanged = true;
                    }
                }

                if (configChanged)
                {
                    _logger.Info("Watch party configuration changed, saving updates");
                    _plugin.SaveConfigurationSafely();
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error validating watch parties", ex);
            }
        }

        private void CleanupRemovedParties()
        {
            lock (_configurationMaintenanceLock)
            {
                lock (_plugin.ConfigurationSyncRoot)
                {
                    CleanupRemovedPartiesCore();
                }
            }
        }

        private void CleanupRemovedPartiesCore()
        {
            try
            {
                var config = _plugin.Configuration;
                var cleanupPlan = PartyRuntimeCleanupPlan.Create(
                    _trackedPartyIds,
                    _trackedActivePartyIds,
                    config.WatchParties);

                foreach (var removedId in cleanupPlan.PartyIdsToTeardown)
                {
                    var reason = cleanupPlan.RemovedPartyIds.Contains(removedId)
                        ? "removed"
                        : "deactivated";
                    _logger.Info($"Party {removedId} was {reason}; cleaning up runtime state...");
                    ClearPartyRuntimeState(removedId);
                }

                _trackedPartyIds.Clear();
                _trackedPartyIds.UnionWith(cleanupPlan.CurrentPartyIds);
                _trackedActivePartyIds.Clear();
                _trackedActivePartyIds.UnionWith(cleanupPlan.CurrentActivePartyIds);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error cleaning up removed parties", ex);
            }
        }

        private void ClearPartyRuntimeState(string partyId)
        {
            var sessions = _plugin.PartyParticipants.GetSessions(partyId);
            _playbackSyncCoordinator.ClearParty(partyId);
            foreach (var participant in sessions)
            {
                _playbackSyncCoordinator.ClearSession(participant.SessionId);
                _seriesEpisodeTransitions.ClearSession(participant.SessionId);
            }

            CancelPendingMasterSeekSync(partyId);
            lock (_seriesTransitionLock)
            {
                _partiesTransitioning.Remove(partyId);
            }

            _partySyncedSessions.TryRemove(partyId, out _);
            _partySessionPauseState.TryRemove(partyId, out _);
            _pauseControlCoordinator.ClearParty(partyId);
            _lastProgressCheckpoint.TryRemove(partyId, out _);
            _seriesSelections.ClearParty(partyId);
            _plugin.PartyParticipants.ClearParty(partyId);
            _plugin.PartyReadyUsers.ClearParty(partyId);
        }

        private void CleanupIneligibleParticipants()
        {
            lock (_configurationMaintenanceLock)
            {
                lock (_plugin.ConfigurationSyncRoot)
                {
                    CleanupIneligibleParticipantsCore();
                }
            }
        }

        private void CleanupIneligibleParticipantsCore()
        {
            var configurationChanged = false;
            foreach (var party in _plugin.Configuration.WatchParties.ToList())
            {
                foreach (var participant in _plugin.PartyParticipants.GetSessions(party.Id))
                {
                    if (WatchPartyAuthorizationPolicy.CanAccessParty(
                            party,
                            participant.UserId,
                            isAdministrator: false))
                    {
                        continue;
                    }

                    if (TryRemoveParticipant(
                            party.Id,
                            participant.SessionId,
                            out var removedMaster)
                        && removedMaster)
                    {
                        configurationChanged |= !HandleMasterDeparture(
                            party,
                            participant.UserId,
                            party.CurrentPositionTicks,
                            "access revoked");
                    }
                }
            }

            if (configurationChanged)
            {
                _plugin.SaveConfigurationSafely();
            }
        }

        private bool QueuePlaybackEvent(
            string sessionId,
            string eventName,
            Func<Task> handler)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                _logger.Warn($"[Watch Party] Ignoring {eventName} event without a session id");
                return false;
            }

            if (!_playbackEventQueue.TryEnqueue(
                    sessionId,
                    _ => handler(),
                    _lifetimeCts.Token,
                    out var completion))
            {
                _logger.Warn(
                    $"[Watch Party] Dropping {eventName} for session {sessionId}: " +
                    "the bounded per-session event queue is full");
                return false;
            }

            _ = ObservePlaybackEventCompletionAsync(completion, sessionId, eventName);
            return true;
        }

        private async Task ObservePlaybackEventCompletionAsync(
            Task completion,
            string sessionId,
            string eventName)
        {
            try
            {
                await completion.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
            {
                // Plugin shutdown cancels queued playback events.
            }
            catch (Exception ex)
            {
                _logger.ErrorException(
                    $"[Watch Party] Queued {eventName} failed for session {sessionId}",
                    ex);
            }
        }

        private void OnPlaybackStart(object sender, PlaybackProgressEventArgs e)
        {
            var pendingSeriesSelection = TryRegisterPendingSeriesSelection(e);
            if (!QueuePlaybackEvent(
                e?.Session?.Id,
                "PlaybackStart",
                () => HandlePlaybackStartAsync(e, pendingSeriesSelection)))
            {
                _seriesSelections.Cancel(pendingSeriesSelection);
            }
        }

        private SeriesSelectionRegistration TryRegisterPendingSeriesSelection(
            PlaybackProgressEventArgs e)
        {
            if (e?.Session == null || e.Item == null)
            {
                return null;
            }

            var party = FindPartyForItem(e.Item);
            if (party == null
                || !party.IsActive
                || !party.IsSeriesParty
                || !IsMasterSelectionSession(party, e.Session))
            {
                return null;
            }

            var episodeItemId = FindSeriesEpisodeId(party, e.Item);
            var currentEpisode = SeriesPartyQueue.GetCurrentEpisode(party);
            if (string.IsNullOrEmpty(episodeItemId)
                || currentEpisode == null
                || string.Equals(
                    currentEpisode.ItemId,
                    episodeItemId,
                    StringComparison.OrdinalIgnoreCase)
                || _seriesEpisodeTransitions.IsExpectedStart(
                    e.Session.Id,
                    episodeItemId,
                    DateTime.UtcNow))
            {
                return null;
            }

            return _seriesSelections.Register(party.Id, episodeItemId);
        }

        private bool IsMasterSelectionSession(WatchPartyItem party, SessionInfo session)
        {
            if (party == null
                || session == null
                || string.IsNullOrEmpty(party.MasterUserId)
                || !string.Equals(
                    party.MasterUserId,
                    session.UserId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var registeredMasterSessionId = _plugin.PartyParticipants.GetMasterSession(party.Id);
            if (string.IsNullOrEmpty(registeredMasterSessionId)
                || string.Equals(
                    registeredMasterSessionId,
                    session.Id,
                    StringComparison.Ordinal))
            {
                return true;
            }

            return !_sessionManager.Sessions.Any(activeSession =>
                string.Equals(
                    activeSession.Id,
                    registeredMasterSessionId,
                    StringComparison.Ordinal)
                && activeSession.NowPlayingItem != null);
        }

        private async Task HandlePlaybackStartAsync(
            PlaybackProgressEventArgs e,
            SeriesSelectionRegistration pendingSeriesSelection)
        {
            try
            {
                _logger.Info($"[Watch Party] PlaybackStart event fired - Item: {e.Item?.Name}, ItemId: {e.Item?.Id}, UserId: {e.Session?.UserId}");

                var party = FindPartyForItem(e.Item);

                if (party != null && party.IsActive)
                {
                    if (!CanUserJoinParty(party, e.Session.UserId))
                    {
                        _logger.Warn($"[Party {party.Id}] Access denied for user {e.Session.UserId}");
                        await _sessionManager.SendPlaystateCommand(e.Session.Id, e.Session.Id, new PlaystateRequest
                        {
                            Command = PlaystateCommand.Stop
                        }, _lifetimeCts.Token);
                        return;
                    }

                    var nowUtc = DateTime.UtcNow;
                    var userStartPosition = e.PlaybackPositionTicks ?? 0;
                    if (GetOrCreateParticipant(
                        party.Id,
                        e.Session,
                        e.PlaySessionId,
                        nowUtc,
                        party.MaxParticipants) == null)
                    {
                        await _sessionManager.SendPlaystateCommand(
                            e.Session.Id,
                            e.Session.Id,
                            new PlaystateRequest { Command = PlaystateCommand.Stop },
                            _lifetimeCts.Token);
                        return;
                    }
                    var isMaster = TryResolveMasterSession(party, e.Session);
                    var startedEpisodeId = FindSeriesEpisodeId(party, e.Item);
                    var currentEpisode = SeriesPartyQueue.GetCurrentEpisode(party);
                    var episodeSwitchTarget = GetSeriesEpisodeSwitchTarget(party, e.Item);
                    var isExpectedSeriesStart = _seriesEpisodeTransitions.ConsumeExpectedStart(
                        e.Session.Id,
                        startedEpisodeId,
                        nowUtc);
                    SeriesSelectionRegistration masterSeriesSelection = null;
                    if (party.IsSeriesParty
                        && isMaster
                        && !isExpectedSeriesStart
                        && !string.IsNullOrEmpty(startedEpisodeId)
                        && currentEpisode != null
                        && !string.Equals(
                            startedEpisodeId,
                            currentEpisode.ItemId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        masterSeriesSelection = pendingSeriesSelection != null
                            && string.Equals(
                                pendingSeriesSelection.PartyId,
                                party.Id,
                                StringComparison.Ordinal)
                            && string.Equals(
                                pendingSeriesSelection.EpisodeItemId,
                                startedEpisodeId,
                                StringComparison.OrdinalIgnoreCase)
                                ? pendingSeriesSelection
                                : _seriesSelections.Register(party.Id, startedEpisodeId);
                    }

                    if (party.IsSeriesParty
                        && !string.IsNullOrEmpty(startedEpisodeId)
                        && currentEpisode != null
                        && (masterSeriesSelection != null
                            || episodeSwitchTarget != null))
                    {
                        if (!isMaster || isExpectedSeriesStart)
                        {
                            var currentPosition = _playbackSyncCoordinator.GetEstimatedPartyPosition(
                                party.Id,
                                party.CurrentPositionTicks,
                                nowUtc);
                            _logger.Info(
                                $"[Party {party.Id}] Session {e.Session.UserId} opened non-current queued episode {startedEpisodeId}; " +
                                $"redirecting to current episode {currentEpisode.ItemId}");
                            await PlaySeriesEpisodeForSessions(
                                party,
                                episodeSwitchTarget ?? currentEpisode,
                                new[] { e.Session },
                                currentPosition);
                            return;
                        }

                        var selectionRan = await _seriesSelections.RunIfLatestAsync(
                            masterSeriesSelection,
                            SeriesSelectionCoalesceWindow,
                            async selectionToken =>
                            {
                                var transitionWasQueued = await EnterSeriesTransitionAsync(
                                    party.Id,
                                    selectionToken);
                                try
                                {
                                    selectionToken.ThrowIfCancellationRequested();
                                    currentEpisode = SeriesPartyQueue.GetCurrentEpisode(party);
                                    if (currentEpisode != null
                                        && !string.Equals(
                                            startedEpisodeId,
                                            currentEpisode.ItemId,
                                            StringComparison.OrdinalIgnoreCase))
                                    {
                                        var transitionSessions = GetSeriesTransitionSessions(party, e.Session)
                                            .Where(session => transitionWasQueued
                                                || (session.Id != e.Session.Id
                                                    && !IsMasterSession(party, session)))
                                            .ToList();
                                        if (!SeriesPartyQueue.TrySelectEpisode(party, startedEpisodeId))
                                        {
                                            _logger.Warn($"[Party {party.Id}] Master selected episode {startedEpisodeId}, but it is not in the queue");
                                            return;
                                        }

                                        selectionToken.ThrowIfCancellationRequested();
                                        ResetSeriesEpisodeSyncState(party);
                                        party.CurrentPositionTicks = userStartPosition;
                                        party.IsPlaying = !party.IsWaitingRoom && !e.IsPaused;
                                        _playbackSyncCoordinator.UpdateMasterPosition(
                                            party.Id,
                                            userStartPosition,
                                            party.IsPlaying,
                                            nowUtc,
                                            TimeSpan.FromSeconds(party.SyncToleranceSeconds).Ticks);
                                        _pauseControlCoordinator.ObserveAuthoritativeState(
                                            party.Id,
                                            isPaused: !party.IsPlaying);
                                        _plugin.SaveConfigurationSafely();
                                        _lastProgressCheckpoint[party.Id] = nowUtc;

                                        _logger.Info(
                                            $"[Party {party.Id}] Master selected queued episode {startedEpisodeId}; " +
                                            $"switching {transitionSessions.Count} participant session(s)");
                                        await PlaySeriesEpisodeForSessions(
                                            party,
                                            SeriesPartyQueue.GetCurrentEpisode(party),
                                            transitionSessions,
                                            userStartPosition,
                                            selectionToken);
                                    }
                                }
                                finally
                                {
                                    ExitSeriesTransition(party.Id);
                                }
                            },
                            _lifetimeCts.Token);
                        if (!selectionRan)
                        {
                            _logger.Debug(
                                $"[Party {party.Id}] Skipping superseded episode selection {startedEpisodeId}");
                            return;
                        }
                    }

                    _logger.Info($"[Watch Party] User {e.Session.UserId} started watching party content: {party.ItemName}");

                    var syncedSessions = _partySyncedSessions.GetOrAdd(
                        party.Id,
                        _ => new ConcurrentDictionary<string, byte>());
                    var pauseState = _partySessionPauseState.GetOrAdd(
                        party.Id,
                        _ => new ConcurrentDictionary<string, bool>());
                    var isInSyncedSet = syncedSessions.ContainsKey(e.Session.Id);
                    var wasPaused = pauseState.TryGetValue(e.Session.Id, out var isPaused) && isPaused;

                    UpdateParticipantActivity(party.Id, e.Session.Id, userStartPosition, e.IsPaused);

                    if (isMaster)
                    {
                        party.CurrentPositionTicks = userStartPosition;
                        if (!party.IsWaitingRoom)
                        {
                            party.IsPlaying = !e.IsPaused;
                        }
                        _playbackSyncCoordinator.UpdateMasterPosition(
                            party.Id,
                            userStartPosition,
                            party.IsPlaying,
                            nowUtc,
                            TimeSpan.FromSeconds(party.SyncToleranceSeconds).Ticks);
                        _pauseControlCoordinator.ObserveAuthoritativeState(
                            party.Id,
                            isPaused: !party.IsPlaying);
                    }

                    if (party.IsWaitingRoom && !party.IsPlaying)
                    {
                        _logger.Info($"[Party {party.Id}] User {e.Session.UserId} started playback - marking as ready");

                        _plugin.PartyReadyUsers.SetReady(party.Id, e.Session.UserId, true);

                        _logger.Info($"[Party {party.Id}] Pausing user in waiting room");
                        await SendPauseStateCommand(e.Session, true);
                        pauseState[e.Session.Id] = true;

                        await CheckWaitingRoomReadiness(party);
                        return;
                    }

                    pauseState[e.Session.Id] = e.IsPaused;
                    _logger.Info($"[Watch Party] Session {e.Session.Id} is in synced set: {isInSyncedSet}, IsMaster: {isMaster}");

                    if (!isInSyncedSet)
                    {
                        syncedSessions.TryAdd(e.Session.Id, 0);
                        _logger.Info($"[Watch Party] Added session {e.Session.Id} to synced set");
                    }

                    if (isMaster)
                    {
                        _logger.Info($"[Watch Party] Session {e.Session.Id} is the master, not syncing");
                        return;
                    }

                    if (!HasActiveMasterSession(party))
                    {
                        _logger.Info($"[Watch Party] Party {party.Id} has no active master; leaving session {e.Session.Id} at its own position");
                        return;
                    }

                    var targetPosition = _playbackSyncCoordinator.GetEstimatedPartyPosition(
                        party.Id,
                        party.CurrentPositionTicks,
                        nowUtc);
                    var needsInitialSync = (!isInSyncedSet || wasPaused)
                        && Math.Abs(userStartPosition - targetPosition) > TimeSpan.FromSeconds(1).Ticks;

                    if (needsInitialSync)
                    {
                        _logger.Info(
                            $"[Watch Party] Initial sync for session {e.Session.Id} " +
                            $"(user at {TimeSpan.FromTicks(userStartPosition).TotalSeconds:F1}s, " +
                            $"party at {TimeSpan.FromTicks(targetPosition).TotalSeconds:F1}s)");
                        await SyncUserToPosition(
                            e.Session,
                            e.Item,
                            targetPosition,
                            controllingSession: GetActiveMasterSession(party));
                    }
                    else
                    {
                        _logger.Debug($"[Watch Party] Session {e.Session.Id} needs no initial sync");
                    }
                }
                else
                {
                    _logger.Debug($"[Watch Party] Not a watch party item or party not active");
                }
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
            {
                // Plugin shutdown cancels in-flight remote-control commands.
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[Watch Party] Error handling playback start", ex);
            }
            finally
            {
                _seriesSelections.Cancel(pendingSeriesSelection);
            }
        }

        private async void CheckAndSyncUsers(object state)
        {
            if (_lifetimeCts.IsCancellationRequested
                || Interlocked.Exchange(ref _syncPassRunning, 1) != 0)
            {
                return;
            }

            try
            {
                ValidateAndCleanWatchParties();

                List<WatchPartyItem> activeParties;
                lock (_plugin.ConfigurationSyncRoot)
                {
                    activeParties = _plugin.Configuration.WatchParties
                        .Where(party => party.IsActive)
                        .ToList();
                }

                foreach (var party in activeParties)
                {
                    CheckAndRemoveInactiveParticipants(party);

                    if (!party.IsPlaying || party.IsWaitingRoom || !HasActiveMasterSession(party))
                    {
                        continue;
                    }

                    lock (_masterSeekSyncLock)
                    {
                        if (_pendingMasterSeeks.ContainsKey(party.Id))
                        {
                            // A master seek (or a suspected stream-reload hold) is settling;
                            // let that path own the next command. Otherwise periodic
                            // calibration would bounce participants to transient positions.
                            _logger.Debug($"[Party {party.Id}] Skipping periodic calibration while master seek is settling");
                            continue;
                        }
                    }

                    var nowUtc = DateTime.UtcNow;
                    var estimatedPartyPosition = _playbackSyncCoordinator.GetEstimatedPartyPosition(
                        party.Id,
                        party.CurrentPositionTicks,
                        nowUtc);
                    _logger.Debug($"[Party {party.Id}] Periodic sync check at estimated position {estimatedPartyPosition} ticks");

                    var sessions = _sessionManager.Sessions.Where(s => s.NowPlayingItem != null).ToList();
                    var maxBufferThreshold = TimeSpan.FromSeconds(party.MaxBufferThresholdSeconds).Ticks;

                    foreach (var session in sessions)
                    {
                        var item = _libraryManager.GetItemById(session.NowPlayingItem.Id);
                        var matchingParty = FindPartyForItem(item);

                        if (matchingParty != null && matchingParty.Id == party.Id)
                        {
                            if (!_plugin.PartyParticipants.TryGetSession(
                                    party.Id,
                                    session.Id,
                                    out var participant))
                            {
                                if (!CanUserJoinParty(party, session.UserId))
                                {
                                    continue;
                                }

                                participant = GetOrCreateParticipant(
                                        party.Id,
                                        session,
                                        nowUtc: nowUtc,
                                        maxParticipants: party.MaxParticipants);
                                if (participant == null)
                                {
                                    continue;
                                }
                            }

                            var reportedPosition = session.PlayState?.PositionTicks ?? 0;
                            var reportedIsPaused = session.PlayState?.IsPaused ?? false;
                            var currentPosition = ParticipantPositionEstimator.Estimate(
                                participant,
                                reportedPosition,
                                reportedIsPaused,
                                nowUtc,
                                MaxParticipantPositionProjection);
                            var positionDifference = Math.Abs(currentPosition - estimatedPartyPosition);
                            var isMaster = TryResolveMasterSession(party, session);

                            if (party.IsSeriesParty && !isMaster)
                            {
                                var episodeSwitchTarget = GetSeriesEpisodeSwitchTarget(party, item);
                                if (episodeSwitchTarget != null)
                                {
                                    if (!_seriesEpisodeTransitions.IsExpectedStart(
                                            session.Id,
                                            episodeSwitchTarget.ItemId,
                                            nowUtc))
                                    {
                                        var playingEpisodeId = FindSeriesEpisodeId(party, item);
                                        _logger.Info(
                                            $"[Party {party.Id}] Session {session.Id} is on episode {playingEpisodeId}; " +
                                            $"switching to current episode {episodeSwitchTarget.ItemId}");
                                        await PlaySeriesEpisodeForSessions(
                                            party,
                                            episodeSwitchTarget,
                                            new[] { session },
                                            estimatedPartyPosition);
                                    }
                                    continue;
                                }
                            }

                            if (ParticipantPositionEstimator.IsNewPlaybackReport(
                                participant,
                                reportedPosition,
                                reportedIsPaused))
                            {
                                UpdateParticipantActivity(
                                    party.Id,
                                    session.Id,
                                    reportedPosition,
                                    reportedIsPaused);
                            }

                            if (!isMaster
                                && positionDifference > maxBufferThreshold
                                && currentPosition < estimatedPartyPosition)
                            {
                                _logger.Warn($"[Party {party.Id}] Session {session.Id} is {TimeSpan.FromTicks(positionDifference).TotalSeconds:F1}s behind (exceeds buffer threshold)");

                                _plugin.PartyParticipants.SetBuffering(
                                    party.Id,
                                    session.Id,
                                    isBuffering: true);
                            }
                            else
                            {
                                _plugin.PartyParticipants.SetBuffering(
                                    party.Id,
                                    session.Id,
                                    isBuffering: false);
                            }

                            if (!isMaster)
                            {
                                var periodicTolerance = TimeSpan.FromSeconds(
                                    Math.Max(1, party.SyncToleranceSeconds));
                                if (positionDifference <= periodicTolerance.Ticks)
                                {
                                    _logger.Debug(
                                        $"[Party {party.Id}] Session {session.Id} is within " +
                                        $"{TimeSpan.FromTicks(positionDifference).TotalSeconds:F1}s of master; " +
                                        "skipping periodic calibration");
                                    continue;
                                }

                                // Periodic calibration: command the participant to the master's estimated
                                // position whenever it strays beyond the tolerance band. A within-band
                                // report is trusted as "close enough" to avoid pointless seeks; a far-out
                                // report (e.g. Emby iOS 2.2.56's stale counter after a remote seek) still
                                // triggers calibration, so the client's own value can never fight the
                                // master clock. The settle window in TryBeginSeek throttles this to at
                                // most one periodic command per participant per 30s, so it never stacks seeks.
                                _logger.Debug(
                                    $"[Party {party.Id}] Periodic calibration: commanding session {session.Id} " +
                                    $"to {TimeSpan.FromTicks(estimatedPartyPosition).TotalSeconds:F1}s " +
                                    $"(diff {TimeSpan.FromTicks(positionDifference).TotalSeconds:F1}s)");
                                var controllingSession = GetActiveMasterSession(party);
                                await SyncUserToPosition(
                                    session,
                                    item,
                                    estimatedPartyPosition,
                                    controllingSession: controllingSession);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[Watch Party] Error in periodic sync check", ex);
            }
            finally
            {
                Volatile.Write(ref _syncPassRunning, 0);
            }
        }

        private async Task SyncUserToPosition(
            SessionInfo session,
            BaseItem item,
            long positionTicks,
            bool allowReplace = false,
            SessionInfo controllingSession = null,
            bool applySyncOffset = true,
            bool force = false)
        {
            try
            {
                if (session == null)
                {
                    return;
                }

                if (!SupportsRemoteControlledPlayback(session))
                {
                    _logger.Info($"[Watch Party] Session {session.Id} ({session.Client}) does not advertise remote video control, skipping seek to {TimeSpan.FromTicks(positionTicks).TotalSeconds:F1}s");
                    return;
                }

                var config = _plugin.Configuration;

                var appliedOffsetMilliseconds = applySyncOffset
                    ? config.SyncOffsetMilliseconds
                    : 0;
                var offsetTicks = TimeSpan.FromMilliseconds(appliedOffsetMilliseconds).Ticks;
                var adjustedPosition = Math.Max(0, positionTicks + offsetTicks);

                var nowUtc = DateTime.UtcNow;
                if (!_playbackSyncCoordinator.TryBeginSeek(
                    session.Id,
                    adjustedPosition,
                    nowUtc,
                    allowReplace,
                    force))
                {
                    _logger.Debug($"[Watch Party] Suppressed duplicate seek for settling session {session.Id}");
                    return;
                }

                var controllingSessionId = controllingSession?.Id ?? session.Id;
                _logger.Info(
                    $"[Watch Party] Syncing session {session.Id} to position {positionTicks} ticks " +
                    $"(adjusted: {adjustedPosition} with {appliedOffsetMilliseconds:+#;-#;0}ms offset, " +
                    $"controller: {controllingSessionId})");

                try
                {
                    await _sessionManager.SendPlaystateCommand(
                        controllingSessionId,
                        session.Id,
                        new PlaystateRequest
                        {
                            Command = PlaystateCommand.Seek,
                            SeekPositionTicks = adjustedPosition,
                            ControllingUserId = controllingSession?.UserId
                        },
                        _lifetimeCts.Token);
                }
                catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
                {
                    _playbackSyncCoordinator.CancelPendingSeek(session.Id, adjustedPosition);
                    return;
                }
                catch
                {
                    _playbackSyncCoordinator.CancelPendingSeek(session.Id, adjustedPosition);
                    throw;
                }

                _logger.Info($"[Watch Party] Server accepted seek command for session {session.Id} (client confirmation pending)");
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"[Watch Party] Error syncing session {session.Id}", ex);
            }
        }

        private WatchPartyItem FindPartyForItem(BaseItem item)
        {
            if (item == null)
            {
                return null;
            }

            List<WatchPartyItem> partySnapshot;
            lock (_plugin.ConfigurationSyncRoot)
            {
                partySnapshot = _plugin.Configuration.WatchParties.ToList();
            }

            return WatchPartyItemMatcher.FindActiveParty(
                partySnapshot,
                item.Id,
                item.InternalId);
        }

        private string FindSeriesEpisodeId(WatchPartyItem party, BaseItem item)
        {
            return item == null
                ? null
                : WatchPartyItemMatcher.FindEpisodeItemId(party, item.Id, item.InternalId);
        }

        private WatchPartyEpisode GetSeriesEpisodeSwitchTarget(WatchPartyItem party, BaseItem item)
        {
            if (party?.IsSeriesParty != true || item == null)
            {
                return null;
            }

            var currentEpisode = SeriesPartyQueue.GetCurrentEpisode(party);
            var playingEpisodeId = FindSeriesEpisodeId(party, item);
            return currentEpisode != null
                && !string.Equals(
                    playingEpisodeId,
                    currentEpisode.ItemId,
                    StringComparison.OrdinalIgnoreCase)
                ? currentEpisode
                : null;
        }

        private void OnPlaybackProgress(object sender, PlaybackProgressEventArgs e)
        {
            QueuePlaybackEvent(
                e?.Session?.Id,
                "PlaybackProgress",
                () => HandlePlaybackProgressAsync(e));
        }

        private async Task HandlePlaybackProgressAsync(PlaybackProgressEventArgs e)
        {
            try
            {
                _logger.Debug($"[Watch Party] PlaybackProgress event fired - Item: {e.Item?.Name}, Position: {e.PlaybackPositionTicks}, Paused: {e.IsPaused}, Session: {e.Session.Id}");
                var party = FindPartyForItem(e.Item);

                if (party != null && party.IsActive)
                {
                    var nowUtc = DateTime.UtcNow;
                    var progressEpisodeId = FindSeriesEpisodeId(party, e.Item);
                    var isExpectedSeriesProgress = party.IsSeriesParty
                        && _seriesEpisodeTransitions.IsExpectedStart(
                            e.Session.Id,
                            progressEpisodeId,
                            nowUtc);

                    // One Emby Web SessionId can briefly retain several PlaySessionIds
                    // after a seek or stream reload. Ignore progress from the replaced
                    // playback before it can overwrite the participant's current playback
                    // id or move the authoritative party clock backwards and forwards.
                    var acceptsPlaybackProgress = _plugin.PartyParticipants.TryAcceptPlaybackProgress(
                        party.Id,
                        e.Session.Id,
                        e.PlaySessionId,
                        e.PlaybackPositionTicks,
                        nowUtc,
                        out var adoptedPlayback,
                        out var previousPlaySessionId);
                    if (!acceptsPlaybackProgress && !isExpectedSeriesProgress)
                    {
                        _logger.Debug(
                            $"[Party {party.Id}] Ignoring stale progress from playback " +
                            $"{e.PlaySessionId} for session {e.Session.Id}");
                        return;
                    }

                    if (adoptedPlayback
                        && _playbackSyncCoordinator.ResetForNewPlayback(
                            e.Session.Id,
                            previousPlaySessionId,
                            e.PlaySessionId))
                    {
                        ClearSessionEpisodeMarkers(party.Id, e.Session.Id);
                        _logger.Info(
                            $"[Party {party.Id}] Adopted playback {e.PlaySessionId} from progress because PlaybackStart was missing; " +
                            $"retired {previousPlaySessionId}");
                    }

                    if (!CanUserJoinParty(party, e.Session.UserId))
                    {
                        _logger.Debug(
                            $"[Party {party.Id}] Ignoring progress from unauthorized or over-capacity user {e.Session.UserId}");
                        return;
                    }

                    if (GetOrCreateParticipant(
                        party.Id,
                        e.Session,
                        e.PlaySessionId,
                        nowUtc,
                        party.MaxParticipants) == null)
                    {
                        return;
                    }

                    var pauseState = _partySessionPauseState.GetOrAdd(
                        party.Id,
                        _ => new ConcurrentDictionary<string, bool>());
                    var hasPriorPauseState = pauseState.TryGetValue(
                        e.Session.Id,
                        out var previousPause);
                    var wasPaused = hasPriorPauseState && previousPause;
                    var reportedPosition = e.PlaybackPositionTicks;
                    var currentPosition = reportedPosition ?? 0;
                    var isMaster = TryResolveMasterSession(party, e.Session);
                    var inboundPauseState = _playbackSyncCoordinator.ClassifyInboundPauseState(
                        e.Session.Id,
                        wasPaused,
                        e.IsPaused,
                        reportedPosition,
                        nowUtc);

                    UpdateParticipantActivity(party.Id, e.Session.Id, currentPosition, e.IsPaused);

                    // Emby only fires PlaybackStart once per PlaySessionId. When a client
                    // resumes party content (or restarts playback) with the same play session
                    // within the idle window, no PlaybackStart event arrives, so the session
                    // never rejoins the synced set and drifts until the next periodic pass.
                    // Re-register on the first progress report and calibrate immediately so
                    // the participant lands on the party clock within a second or two instead
                    // of playing at a stale position for 10-30 seconds.
                    if (!isMaster && !party.IsWaitingRoom)
                    {
                        var syncedSessions = _partySyncedSessions.GetOrAdd(
                            party.Id,
                            _ => new ConcurrentDictionary<string, byte>());
                        if (!syncedSessions.ContainsKey(e.Session.Id)
                            && HasActiveMasterSession(party)
                            && !_playbackSyncCoordinator.IsSeekSettling(e.Session.Id, nowUtc))
                        {
                            var targetPosition = _playbackSyncCoordinator.GetEstimatedPartyPosition(
                                party.Id,
                                party.CurrentPositionTicks,
                                nowUtc);
                            var drift = Math.Abs(currentPosition - targetPosition);

                            if (drift > TimeSpan.FromSeconds(2).Ticks)
                            {
                                _logger.Info(
                                    $"[Party {party.Id}] Session {e.Session.Id} resumed party playback without a PlaybackStart event " +
                                    $"(user at {TimeSpan.FromTicks(currentPosition).TotalSeconds:F1}s, party at " +
                                    $"{TimeSpan.FromTicks(targetPosition).TotalSeconds:F1}s); re-syncing immediately");
                                await SyncUserToPosition(
                                    e.Session,
                                    e.Item,
                                    targetPosition,
                                    controllingSession: GetActiveMasterSession(party));
                            }
                            else
                            {
                                _logger.Debug(
                                    $"[Party {party.Id}] Session {e.Session.Id} resumed within " +
                                    $"{TimeSpan.FromTicks(drift).TotalSeconds:F1}s of party position; no re-sync needed");
                            }

                            syncedSessions.TryAdd(e.Session.Id, 0);
                        }
                    }

                    if (_playbackSyncCoordinator.ConfirmSeekTarget(e.Session.Id, currentPosition, nowUtc))
                    {
                        _logger.Debug($"[Party {party.Id}] Session {e.Session.Id} confirmed seek target at {TimeSpan.FromTicks(currentPosition).TotalSeconds:F1}s");
                    }

                    if (WaitingRoomPolicy.SuppressesPlaybackControl(party))
                    {
                        // Waiting-room progress is readiness/activity, never a room-control
                        // signal. A local resume (or an uncontrollable client that never
                        // honored the initial Pause) must not reach Anyone/Host/Vote and
                        // broadcast an early Unpause. Reassert once per reported transition;
                        // repeated unpaused progress is not allowed to create a command storm.
                        if (WaitingRoomPolicy.ShouldRestorePause(party, e.IsPaused)
                            && (!hasPriorPauseState || wasPaused))
                        {
                            _logger.Info(
                                $"[Party {party.Id}] Ignoring resume from session {e.Session.Id} " +
                                "while the waiting room owns playback state");
                            await SendPauseStateCommand(e.Session, true);
                        }
                    }
                    else if (inboundPauseState.IsTransition
                        && inboundPauseState.IsExpectedCommandEcho)
                    {
                        _logger.Debug(
                            $"[Party {party.Id}] Ignoring playback-state echo from session {e.Session.Id} " +
                            "because it matches an explicit server Pause/Unpause command");
                    }
                    else if (e.IsPaused && !wasPaused)
                    {
                        if (inboundPauseState.IsSeekCommandEcho)
                        {
                            _logger.Debug(
                                $"[Party {party.Id}] Treating position-less pause from session {e.Session.Id} " +
                                "during seek settlement as user input because no explicit pause command matched");
                        }
                        await HandlePauseAttempt(
                            party,
                            e.Session,
                            isMaster,
                            reportedPosition);
                    }
                    else if (!e.IsPaused && wasPaused)
                    {
                        if (inboundPauseState.IsSeekCommandEcho)
                        {
                            _logger.Debug(
                                $"[Party {party.Id}] Treating position-less unpause from session {e.Session.Id} " +
                                "during seek settlement as user input because no explicit unpause command matched");
                        }
                        await HandleUnpauseAttempt(party, e.Session, isMaster);
                    }

                    pauseState[e.Session.Id] = e.IsPaused;

                    if (isMaster)
                    {
                        if (!party.IsWaitingRoom)
                        {
                            party.IsPlaying = !e.IsPaused;
                        }
                        _pauseControlCoordinator.ObserveAuthoritativeState(
                            party.Id,
                            isPaused: !party.IsPlaying);

                        if (reportedPosition.HasValue)
                        {
                            var masterPositionUpdate = _playbackSyncCoordinator.UpdateMasterPositionGuardingReloadArtifact(
                                party.Id,
                                currentPosition,
                                !e.IsPaused && !party.IsWaitingRoom,
                                nowUtc,
                                TimeSpan.FromSeconds(party.SyncToleranceSeconds).Ticks,
                                MasterSeekNearZeroThreshold.Ticks,
                                MasterSeekDrasticFromThreshold.Ticks);

                            if (masterPositionUpdate.IsDeferredReloadArtifact)
                            {
                                party.CurrentPositionTicks = masterPositionUpdate.AuthoritativePositionTicks;
                                _logger.Debug(
                                    $"[Party {party.Id}] Staged transient near-zero master position " +
                                    $"{TimeSpan.FromTicks(currentPosition).TotalSeconds:F1}s while preserving " +
                                    $"{TimeSpan.FromTicks(party.CurrentPositionTicks).TotalSeconds:F1}s");
                                ScheduleMasterSeekSync(
                                    party,
                                    e.Session.Id,
                                    currentPosition,
                                    isReloadArtifact: true,
                                    authoritativeRevision: masterPositionUpdate.AuthoritativeRevision,
                                    stagedAuthoritativePositionTicks: masterPositionUpdate.AuthoritativePositionTicks);
                                return;
                            }

                            party.CurrentPositionTicks = currentPosition;

                            _logger.Debug($"[Watch Party] Master user updated party {party.Id} position to {party.CurrentPositionTicks} ticks, Playing: {party.IsPlaying}");
                            CheckpointPartyProgress(party);

                            // A transition into pause already performed one explicit,
                            // offset-free sync above. Do not classify the same pause report
                            // as a second seek. Timeline drags while already paused still sync.
                            if (masterPositionUpdate.IsSeek && !(e.IsPaused && !wasPaused))
                            {
                                _logger.Info(
                                    $"[Party {party.Id}] Master seeked to " +
                                    $"{TimeSpan.FromTicks(currentPosition).TotalSeconds:F1}s; scheduling participant sync");
                                ScheduleMasterSeekSync(
                                    party,
                                    e.Session.Id,
                                    currentPosition);
                            }
                            else if (!masterPositionUpdate.IsSeek
                                && CancelPendingMasterReloadArtifact(party.Id))
                            {
                                _logger.Debug(
                                    $"[Party {party.Id}] Discarded transient near-zero master position " +
                                    "after the continuous position recovered");
                            }
                        }
                        else
                        {
                            // Emby Web sends position-less StateChange reports for Pause,
                            // Unpause and while a seek is in flight. Preserve the projected
                            // position, but still apply the playing state; otherwise a paused
                            // master clock keeps advancing and later participants seek ahead by
                            // exactly the wall-clock duration of the pause.
                            var preservedPosition = _playbackSyncCoordinator.SetMasterPlaybackState(
                                party.Id,
                                !e.IsPaused && !party.IsWaitingRoom,
                                party.CurrentPositionTicks,
                                nowUtc);
                            party.CurrentPositionTicks = preservedPosition;
                            _logger.Debug(
                                $"[Watch Party] Master session {e.Session.Id} reported progress without a position; " +
                                $"preserved {TimeSpan.FromTicks(preservedPosition).TotalSeconds:F1}s and " +
                                $"set Playing={party.IsPlaying}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[Watch Party] Error handling playback progress", ex);
            }
        }

        private void ScheduleMasterSeekSync(
            WatchPartyItem party,
            string masterSessionId,
            long positionTicks,
            bool isReloadArtifact = false,
            long authoritativeRevision = 0,
            long stagedAuthoritativePositionTicks = 0)
        {
            var debounce = isReloadArtifact
                ? MasterSeekDrasticDebounce
                : MasterSeekDebounce;

            lock (_masterSeekSyncLock)
            {
                if (_pendingMasterSeeks.TryGetValue(party.Id, out var pending))
                {
                    if (isReloadArtifact && pending.IsReloadArtifact)
                    {
                        // Both reports are transient near-zero positions from the same
                        // stream reload: keep the original hold deadline and just track
                        // the latest reported position. Restarting the window on every
                        // reload report would defer the sync indefinitely.
                        pending.MasterSessionId = masterSessionId;
                        pending.PositionTicks = positionTicks;
                        pending.AuthoritativeRevision = authoritativeRevision;
                        pending.StagedAuthoritativePositionTicks = stagedAuthoritativePositionTicks;
                        return;
                    }

                    // A different class of seek: replace the target and restart the
                    // debounce window so the participant receives one final seek once
                    // the drag settles instead of a burst of intermediate positions.
                    pending.MasterSessionId = masterSessionId;
                    pending.PositionTicks = positionTicks;
                    pending.Debounce = debounce;
                    pending.IsReloadArtifact = isReloadArtifact;
                    pending.AuthoritativeRevision = authoritativeRevision;
                    pending.StagedAuthoritativePositionTicks = stagedAuthoritativePositionTicks;
                    pending.WindowCts.Cancel();
                    pending.WindowCts.Dispose();
                    pending.WindowCts = new CancellationTokenSource();
                    _ = CompleteMasterSeekSyncAsync(party, pending, pending.WindowCts.Token);
                    return;
                }

                pending = new PendingMasterSeekSync
                {
                    MasterSessionId = masterSessionId,
                    PositionTicks = positionTicks,
                    Debounce = debounce,
                    IsReloadArtifact = isReloadArtifact,
                    AuthoritativeRevision = authoritativeRevision,
                    StagedAuthoritativePositionTicks = stagedAuthoritativePositionTicks,
                    WindowCts = new CancellationTokenSource()
                };
                _pendingMasterSeeks[party.Id] = pending;
                _ = CompleteMasterSeekSyncAsync(party, pending, pending.WindowCts.Token);
            }
        }

        private void CancelPendingMasterSeekSync(string partyId)
        {
            lock (_masterSeekSyncLock)
            {
                if (_pendingMasterSeeks.TryGetValue(partyId, out var pending))
                {
                    pending.WindowCts.Cancel();
                    pending.WindowCts.Dispose();
                    _pendingMasterSeeks.Remove(partyId);
                }
            }
        }

        private bool CancelPendingMasterReloadArtifact(string partyId)
        {
            lock (_masterSeekSyncLock)
            {
                if (!_pendingMasterSeeks.TryGetValue(partyId, out var pending)
                    || !pending.IsReloadArtifact)
                {
                    return false;
                }

                pending.WindowCts.Cancel();
                pending.WindowCts.Dispose();
                _pendingMasterSeeks.Remove(partyId);
                return true;
            }
        }

        private async Task CompleteMasterSeekSyncAsync(
            WatchPartyItem party,
            PendingMasterSeekSync pending,
            CancellationToken token)
        {
            try
            {
                await Task.Delay(pending.Debounce, token);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer master seek report; that report owns the next sync.
                return;
            }

            string masterSessionId;
            long positionTicks;
            bool isReloadArtifact;
            long authoritativeRevision;
            long stagedAuthoritativePositionTicks;
            lock (_masterSeekSyncLock)
            {
                if (!_pendingMasterSeeks.TryGetValue(party.Id, out var current)
                    || !ReferenceEquals(current, pending))
                {
                    return;
                }

                _pendingMasterSeeks.Remove(party.Id);
                masterSessionId = pending.MasterSessionId;
                isReloadArtifact = pending.IsReloadArtifact;
                authoritativeRevision = pending.AuthoritativeRevision;
                stagedAuthoritativePositionTicks = pending.StagedAuthoritativePositionTicks;
            }

            var nowUtc = DateTime.UtcNow;
            if (isReloadArtifact)
            {
                // No continuous position replaced the candidate within the hold window,
                // so this was a genuine seek to the beginning rather than Web's reload
                // artifact. Commit it only now; until this point the authoritative clock
                // remained untouched.
                if (!_playbackSyncCoordinator.TryCommitDeferredMasterPosition(
                    party.Id,
                    authoritativeRevision,
                    pending.PositionTicks,
                    stagedAuthoritativePositionTicks,
                    nowUtc,
                    out positionTicks))
                {
                    _logger.Debug(
                        $"[Party {party.Id}] Discarded expired near-zero master position " +
                        "because a newer authoritative report already recovered");
                    return;
                }
                party.CurrentPositionTicks = positionTicks;
                CheckpointPartyProgress(party);
                _logger.Info(
                    $"[Party {party.Id}] Confirmed near-zero master seek at " +
                    $"{TimeSpan.FromTicks(positionTicks).TotalSeconds:F1}s after reload hold");
            }
            else
            {
                // Sync to where the master clock projects to at fire time so a drag
                // lands participants at the master's current, continuously advancing
                // position rather than the stale sample that opened the debounce window.
                positionTicks = _playbackSyncCoordinator.GetEstimatedPartyPosition(
                    party.Id,
                    pending.PositionTicks,
                    nowUtc);
            }

            try
            {
                await SyncParticipantsToPosition(party, masterSessionId, positionTicks);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[Watch Party] Error syncing participants after master seek", ex);
            }
        }

        private async Task SyncParticipantsToPosition(
            WatchPartyItem party,
            string masterSessionId,
            long positionTicks)
        {
            var sessions = _sessionManager.Sessions.Where(session => session.NowPlayingItem != null).ToList();
            foreach (var session in sessions)
            {
                if (session.Id == masterSessionId || IsMasterSession(party, session))
                {
                    continue;
                }

                var item = _libraryManager.GetItemById(session.NowPlayingItem.Id);
                var matchingParty = FindPartyForItem(item);
                if (matchingParty != null
                    && matchingParty.Id == party.Id
                    && _plugin.PartyParticipants.TryGetSession(
                        party.Id,
                        session.Id,
                        out _))
                {
                    var episodeSwitchTarget = GetSeriesEpisodeSwitchTarget(party, item);
                    if (episodeSwitchTarget != null)
                    {
                        await PlaySeriesEpisodeForSessions(
                            party,
                            episodeSwitchTarget,
                            new[] { session },
                            positionTicks);
                        continue;
                    }

                    var controllingSession = _sessionManager.Sessions.FirstOrDefault(candidate =>
                        string.Equals(candidate.Id, masterSessionId, StringComparison.Ordinal));
                    await SyncUserToPosition(
                        session,
                        item,
                        positionTicks,
                        allowReplace: true,
                        controllingSession: controllingSession);
                }
            }
        }

        private void CheckpointPartyProgress(WatchPartyItem party)
        {
            var now = DateTime.UtcNow;
            if (_lastProgressCheckpoint.TryGetValue(party.Id, out var lastCheckpoint)
                && now - lastCheckpoint < ProgressCheckpointInterval)
            {
                return;
            }

            _lastProgressCheckpoint[party.Id] = now;
            _plugin.SaveConfigurationSafely();
            _logger.Debug($"[Party {party.Id}] Persisted playback progress checkpoint");
        }

        private List<SessionInfo> GetSeriesTransitionSessions(WatchPartyItem party, SessionInfo masterSession)
        {
            var sessionIds = new HashSet<string>(StringComparer.Ordinal);
            if (masterSession != null)
            {
                sessionIds.Add(masterSession.Id);
            }

            if (_partySyncedSessions.TryGetValue(party.Id, out var syncedSessions))
            {
                sessionIds.UnionWith(syncedSessions.Keys);
            }

            foreach (var participant in _plugin.PartyParticipants.GetSessions(party.Id))
            {
                if (!string.IsNullOrEmpty(participant.SessionId))
                {
                    sessionIds.Add(participant.SessionId);
                }
            }

            return _sessionManager.Sessions.Where(session => sessionIds.Contains(session.Id)).ToList();
        }

        private static bool SupportsRemoteControlledPlayback(SessionInfo session)
        {
            return session != null
                && PlaybackControlCapabilities.CanReceivePlaybackCommand(
                    session.SupportsRemoteControl,
                    session.PlayableMediaTypes);
        }

        private async Task<bool> EnterSeriesTransitionAsync(
            string partyId,
            CancellationToken cancellationToken = default)
        {
            var waited = false;
            while (true)
            {
                lock (_seriesTransitionLock)
                {
                    if (_partiesTransitioning.Add(partyId))
                    {
                        return waited;
                    }
                }

                waited = true;
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }

        private void ExitSeriesTransition(string partyId)
        {
            lock (_seriesTransitionLock)
            {
                _partiesTransitioning.Remove(partyId);
            }
        }

        private async Task PlaySeriesEpisodeForSessions(
            WatchPartyItem party,
            WatchPartyEpisode episode,
            IReadOnlyCollection<SessionInfo> sessions,
            long startPositionTicks = 0,
            CancellationToken cancellationToken = default)
        {
            var commandCancellationToken = cancellationToken.CanBeCanceled
                ? cancellationToken
                : _lifetimeCts.Token;
            var episodeItem = episode == null ? null : _libraryManager.GetItemById(episode.ItemId);
            if (episodeItem == null)
            {
                _logger.Warn($"[Party {party.Id}] Cannot play next episode because item {episode?.ItemId} was not found");
                return;
            }

            var nowUtc = DateTime.UtcNow;
            _seriesEpisodeTransitions.RemoveExpired(nowUtc);
            var expectedStartExpiration = nowUtc.AddSeconds(30);

            foreach (var session in sessions)
            {
                if (session == null || !SupportsRemoteControlledPlayback(session))
                {
                    _logger.Info($"[Party {party.Id}] Skipping {session?.UserName} ({session?.Client}): session does not support remote-controlled playback");
                    continue;
                }

                try
                {
                    _seriesEpisodeTransitions.ExpectStart(
                        session.Id,
                        episode.ItemId,
                        expectedStartExpiration);
                    await _sessionManager.SendPlayCommand(
                        session.Id,
                        session.Id,
                        new PlayRequest
                        {
                            ItemIds = new[] { episodeItem.InternalId },
                            PlayCommand = PlayCommand.PlayNow,
                            StartPositionTicks = Math.Max(0, startPositionTicks)
                        },
                        commandCancellationToken);
                    _logger.Info($"[Party {party.Id}] Sent episode {episode.ItemName} to {session.UserName}");
                }
                catch (OperationCanceledException) when (commandCancellationToken.IsCancellationRequested)
                {
                    _seriesEpisodeTransitions.CancelExpectedStart(
                        session.Id,
                        episode.ItemId);
                    if (_lifetimeCts.IsCancellationRequested)
                    {
                        return;
                    }

                    throw;
                }
                catch (Exception ex)
                {
                    _seriesEpisodeTransitions.CancelExpectedStart(
                        session.Id,
                        episode.ItemId);
                    _logger.ErrorException(
                        $"[Party {party.Id}] Client {session.Client} did not accept episode {episode.ItemName}",
                        ex);
                }
            }
        }

        private void ResetSeriesEpisodeSyncState(WatchPartyItem party)
        {
            if (_partySyncedSessions.TryGetValue(party.Id, out var syncedSessions))
            {
                syncedSessions.Clear();
            }
            if (_partySessionPauseState.TryGetValue(party.Id, out var pauseStates))
            {
                pauseStates.Clear();
            }

            _pauseControlCoordinator.ResetParty(party.Id, isPaused: false);
            _playbackSyncCoordinator.ClearParty(party.Id);
            foreach (var participant in _plugin.PartyParticipants.GetSessions(party.Id))
            {
                _playbackSyncCoordinator.ClearSession(participant.SessionId);
            }
            _plugin.PartyParticipants.ResetEpisodeState(party.Id);
        }

        private long GetLastKnownPosition(WatchPartyItem party, SessionInfo session)
        {
            if (session?.PlayState?.PositionTicks is long sessionPosition && sessionPosition > 0)
            {
                return sessionPosition;
            }

            if (session != null
                && _plugin.PartyParticipants.TryGetSession(party.Id, session.Id, out var participant)
                && participant.CurrentPositionTicks > 0)
            {
                return participant.CurrentPositionTicks;
            }

            return party.CurrentPositionTicks;
        }

        private void OnPlaybackStopped(object sender, PlaybackStopEventArgs e)
        {
            QueuePlaybackEvent(
                e?.Session?.Id,
                "PlaybackStopped",
                () => HandlePlaybackStoppedAsync(e));
        }

        private async Task HandlePlaybackStoppedAsync(PlaybackStopEventArgs e)
        {
            try
            {
                _logger.Info($"[Watch Party] PlaybackStopped event fired - Item: {e.Item?.Name}, ItemId: {e.Item?.Id}, Session: {e.Session.Id}");

                var party = FindPartyForItem(e.Item);

                if (party != null && party.IsActive)
                {
                    if (_plugin.PartyParticipants.TryGetSession(
                            party.Id,
                            e.Session.Id,
                            out var currentParticipant)
                        && !string.IsNullOrEmpty(currentParticipant.PlaySessionId)
                        && !string.Equals(
                            e.PlaySessionId,
                            currentParticipant.PlaySessionId,
                            StringComparison.Ordinal))
                    {
                        _logger.Info(
                            $"[Party {party.Id}] Ignoring delayed Stop for playback " +
                            $"{e.PlaySessionId}; session {e.Session.Id} now represents " +
                            $"{currentParticipant.PlaySessionId}");
                        return;
                    }

                    var isMaster = TryResolveMasterSession(party, e.Session);
                    var stoppedPosition = GetLastKnownPosition(party, e.Session);
                    var stoppedEpisodeId = FindSeriesEpisodeId(party, e.Item);
                    var currentEpisode = SeriesPartyQueue.GetCurrentEpisode(party);
                    var sourceItem = currentEpisode == null ? null : _libraryManager.GetItemById(currentEpisode.ItemId);
                    var runtimeTicks = e.Item?.RunTimeTicks ?? sourceItem?.RunTimeTicks ?? 0;

                    if (party.IsSeriesParty)
                    {
                        if (_seriesEpisodeTransitions.ShouldRetainSessionOnStop(
                                e.Session.Id,
                                stoppedEpisodeId,
                                currentEpisode?.ItemId,
                                DateTime.UtcNow))
                        {
                            _logger.Info(
                                $"[Party {party.Id}] Retaining session {e.Session.Id} while it switches " +
                                $"from episode {stoppedEpisodeId} to {currentEpisode?.ItemId}");
                            return;
                        }

                        var completedEpisodeId = stoppedEpisodeId;
                        var completionResult = SeriesPartyAdvanceResult.NotCompleted;
                        var transitionAlreadyInProgress = false;

                        lock (_seriesTransitionLock)
                        {
                            transitionAlreadyInProgress = _partiesTransitioning.Contains(party.Id);
                            if (isMaster && !transitionAlreadyInProgress)
                            {
                                completionResult = SeriesPartyQueue.TryAdvanceAfterStop(
                                    party,
                                    completedEpisodeId,
                                    stoppedPosition,
                                    runtimeTicks);

                                if (completionResult == SeriesPartyAdvanceResult.Advanced)
                                {
                                    _partiesTransitioning.Add(party.Id);
                                }
                            }
                        }

                        if (transitionAlreadyInProgress)
                        {
                            _logger.Debug($"[Party {party.Id}] Ignoring duplicate stop while the next episode is starting");
                            return;
                        }

                        if (completionResult == SeriesPartyAdvanceResult.Advanced)
                        {
                            try
                            {
                                var transitionSessions = GetSeriesTransitionSessions(party, e.Session);
                                var nextEpisode = SeriesPartyQueue.GetCurrentEpisode(party);
                                _logger.Info(
                                    $"[Party {party.Id}] Master completed {completedEpisodeId}; advancing to {nextEpisode?.ItemName}");

                                _plugin.SaveConfigurationSafely();
                                _lastProgressCheckpoint[party.Id] = DateTime.UtcNow;
                                ResetSeriesEpisodeSyncState(party);
                                await PlaySeriesEpisodeForSessions(party, nextEpisode, transitionSessions);
                            }
                            finally
                            {
                                ExitSeriesTransition(party.Id);
                            }
                            return;
                        }

                        if (completionResult == SeriesPartyAdvanceResult.EndOfQueue)
                        {
                            _logger.Info($"[Party {party.Id}] Series queue completed at {party.CurrentEpisodeId}");
                            _playbackSyncCoordinator.StopMasterClock(
                                party.Id,
                                stoppedPosition,
                                DateTime.UtcNow);
                            _pauseControlCoordinator.ObserveAuthoritativeState(
                                party.Id,
                                isPaused: true);
                            _plugin.SaveConfigurationSafely();
                            _lastProgressCheckpoint[party.Id] = DateTime.UtcNow;
                        }
                        else if (isMaster && completionResult == SeriesPartyAdvanceResult.ItemMismatch)
                        {
                            _logger.Debug(
                                $"[Party {party.Id}] Ignoring delayed stop for previous episode {stoppedEpisodeId}");
                            return;
                        }
                        else if (!isMaster && SeriesPartyQueue.IsNaturalCompletion(stoppedPosition, runtimeTicks))
                        {
                            _logger.Info(
                                $"[Party {party.Id}] Participant {e.Session.UserId} reached the episode end; retaining membership for transition");
                            return;
                        }
                    }

                    if (!TryRemoveParticipant(
                            party.Id,
                            e.Session.Id,
                            out var removedMaster,
                            e.PlaySessionId,
                            requirePlaySessionMatch: true))
                    {
                        _logger.Debug(
                            $"[Party {party.Id}] Stop for session {e.Session.Id} no longer matched current membership; ignoring teardown");
                        return;
                    }

                    isMaster = removedMaster;

                    if (isMaster)
                    {
                        if (HandleMasterDeparture(
                                party,
                                e.Session.UserId,
                                stoppedPosition,
                                "playback stopped",
                                freezeAtFallbackPosition: true))
                        {
                            return;
                        }

                        _plugin.SaveConfigurationSafely();
                    }
                    else
                    {
                        _logger.Info($"[Watch Party] Participant {e.Session.UserId} stopped watching party content");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[Watch Party] Error handling playback stop", ex);
            }
        }

        public void Dispose()
        {
            _sessionManager.PlaybackStart -= OnPlaybackStart;
            _sessionManager.PlaybackProgress -= OnPlaybackProgress;
            _sessionManager.PlaybackStopped -= OnPlaybackStopped;
            _plugin.ConfigurationUpdated -= OnConfigurationUpdated;

            _waitingRoomStartRegistration?.Dispose();
            _waitingRoomStartRegistration = null;

            _lifetimeCts.Cancel();
            _syncTimer?.Dispose();
            lock (_masterSeekSyncLock)
            {
                foreach (var pending in _pendingMasterSeeks.Values)
                {
                    pending.WindowCts.Cancel();
                    pending.WindowCts.Dispose();
                }
                _pendingMasterSeeks.Clear();
            }

            try
            {
                _plugin.SaveConfigurationSafely();
                _logger.Info("Persisted Watch Party state during shutdown");
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error persisting Watch Party state during shutdown", ex);
            }

            _logger.Info("Watch Party plugin stopped");
        }

        private sealed class PendingMasterSeekSync
        {
            public string MasterSessionId { get; set; }
            public long PositionTicks { get; set; }
            public TimeSpan Debounce { get; set; }
            public bool IsReloadArtifact { get; set; }
            public long AuthoritativeRevision { get; set; }
            public long StagedAuthoritativePositionTicks { get; set; }
            public CancellationTokenSource WindowCts { get; set; }
        }
    }
}
