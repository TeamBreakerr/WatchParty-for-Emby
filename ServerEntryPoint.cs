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
        private readonly HashSet<string> _partiesTransitioning = new HashSet<string>();
        private readonly SeriesSelectionCoordinator _seriesSelections =
            new SeriesSelectionCoordinator();
        private readonly SeriesEpisodeTransitionTracker _seriesEpisodeTransitions =
            new SeriesEpisodeTransitionTracker();
        private readonly object _seriesTransitionLock = new object();
        private readonly object _configurationMaintenanceLock = new object();
        private readonly ConcurrentDictionary<string, DateTime> _lastProgressCheckpoint = new ConcurrentDictionary<string, DateTime>();
        private readonly WatchPartyMediaIdentityResolver _mediaIdentityResolver;
        private readonly PlaybackSyncCoordinator _playbackSyncCoordinator = new PlaybackSyncCoordinator();
        private readonly MasterSeekSourceTracker _masterSeekSources =
            new MasterSeekSourceTracker();
        private readonly ParticipantDormancyTracker _participantDormancies =
            new ParticipantDormancyTracker(ParticipantDormancyRetentionPeriod);
        private readonly CancellationTokenSource _lifetimeCts = new CancellationTokenSource();
        private readonly KeyedAsyncSerialQueue<string> _playbackEventQueue =
            new KeyedAsyncSerialQueue<string>(capacityPerKey: 128);
        // All remote-control commands for one Emby SessionId share this queue. The
        // inbound event queue serializes reports, but seek/pause/resume can originate
        // from its debounce tasks and the periodic calibration timer concurrently.
        private readonly DormancyAwarePlaybackCommandQueue _playbackCommandQueue;
        private readonly ParticipantResumeCoordinator _participantResumeCoordinator =
            new ParticipantResumeCoordinator(capacityPerSession: 16);
        private readonly ParticipantResumeLatencyEstimator _participantResumeLatencies =
            new ParticipantResumeLatencyEstimator(
                initialEstimate: TimeSpan.FromMilliseconds(250),
                minimumEstimate: TimeSpan.FromMilliseconds(100),
                maximumEstimate: TimeSpan.FromSeconds(6),
                sampleTimeout: TimeSpan.FromSeconds(30),
                smoothingFactor: 0.5);
        private readonly AcknowledgedPlaybackStateCommandRetrier _playbackStateCommandRetrier =
            new AcknowledgedPlaybackStateCommandRetrier(
                PauseCommandMaxAttempts,
                PauseCommandRetryDelay);
        private readonly ConfirmedPlaybackSeekRetrier _confirmedPlaybackSeekRetrier =
            new ConfirmedPlaybackSeekRetrier(
                ResumeSeekMaxAttempts,
                ResumeSeekConfirmationTimeout);
        private readonly OfficialIosWebSocketTransport _officialIosWebSocketTransport =
            new OfficialIosWebSocketTransport();
        private readonly PartyManualSynchronizationCoordinator _manualSynchronizationCoordinator =
            new PartyManualSynchronizationCoordinator();
        private readonly PartyPlaybackTransitionCoordinator _partyPlaybackTransitions;
        private readonly MasterPlaybackStateDebouncer _masterPlaybackStateDebouncer =
            new MasterPlaybackStateDebouncer(TimeSpan.FromMilliseconds(750));
        private readonly object _masterPauseCommitLock = new object();
        private readonly Dictionary<string, PendingMasterPauseCommit> _pendingMasterPauseCommits =
            new Dictionary<string, PendingMasterPauseCommit>(StringComparer.Ordinal);
        private IDisposable _waitingRoomStartRegistration;
        private int _syncPassRunning;
        private static readonly TimeSpan ProgressCheckpointInterval = TimeSpan.FromSeconds(30);
        // Emby emits PlaybackStopped after roughly one minute when iOS backgrounds a
        // player. Retain the participant for the lifetime of the active master clock;
        // the returning client can then reuse its SessionId and rejoin immediately.
        private static readonly TimeSpan ParticipantDormancyRetentionPeriod =
            Timeout.InfiniteTimeSpan;
        // Emby's command endpoint can acknowledge a WebSocket command even when the
        // client drops that frame while reconnecting. A small idempotent retry window
        // makes pause/play reliable without flooding the client or reordering seeks.
        private const int PauseCommandMaxAttempts = 3;
        private static readonly TimeSpan PauseCommandRetryDelay =
            TimeSpan.FromMilliseconds(350);
        private const int ResumeSeekMaxAttempts = 2;
        // Relayed iOS clients have shown 2-3s of additional delay. Six seconds keeps a
        // retry beyond that observed tail and one normal sync interval, while the strict
        // two-attempt bound still avoids a seek storm when the client never confirms.
        private static readonly TimeSpan ResumeSeekConfirmationTimeout =
            TimeSpan.FromSeconds(6);
        private const int SeriesCommandMaxAttempts = 3;
        private static readonly TimeSpan SeriesCommandConfirmationRetryInterval =
            TimeSpan.FromSeconds(6);
        private static readonly TimeSpan PartyValidationInterval = TimeSpan.FromMinutes(5);
        // Clients normally report progress every 5-10 seconds. Periodic calibration
        // projects a cached participant position for at most this long before comparing
        // it with the master, avoiding false drift without hiding a real seek.
        private static readonly TimeSpan MaxParticipantPositionProjection = TimeSpan.FromSeconds(15);
        // Emby clients normally report every 5-10 seconds. A gap of two reporting
        // intervals is a strong signal that the client/WebSocket disappeared and has
        // now returned; the next report must get a fresh authoritative sync even when
        // the SessionId and PlaySessionId did not change.
        private static readonly TimeSpan ParticipantReconciliationGap =
            TimeSpan.FromSeconds(20);
        // A WebSocket can disappear briefly while iOS rebuilds its player or crosses
        // a relayed IPv4 path. Wait for one bounded recovery window before declaring
        // a master command lost; Firebase remains intentionally disabled.
        private static readonly TimeSpan OfficialIosWebSocketRecoveryTimeout =
            TimeSpan.FromSeconds(4);
        private static readonly TimeSpan OfficialIosWebSocketRecoveryPollInterval =
            TimeSpan.FromMilliseconds(250);
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

        // The Emby service layer creates WatchPartyService independently from this
        // entry point. Keep one short-lived bridge so the dedicated Web seek endpoint
        // can enter the same per-session playback queue as native progress events.
        public static ServerEntryPoint Current { get; private set; }

        public bool IsParticipantDormant(string partyId, string sessionId)
        {
            return _participantDormancies.IsDormant(partyId, sessionId);
        }

        public IReadOnlyList<PartyLaunchTarget> GetPartyLaunchTargets(string partyId)
        {
            WatchPartyItem party;
            lock (_plugin.ConfigurationSyncRoot)
            {
                party = _plugin.Configuration.WatchParties.FirstOrDefault(candidate =>
                    candidate.IsActive
                    && string.Equals(candidate.Id, partyId, StringComparison.Ordinal));
            }
            if (party == null)
            {
                return Array.Empty<PartyLaunchTarget>();
            }

            var masterSessionId = GetActiveMasterSession(party)?.Id;
            return PartyLaunchTargetProjector.Project(
                _sessionManager.Sessions,
                masterSessionId,
                party.MasterUserId,
                session => CanUserJoinParty(party, session.UserId, logDenied: false),
                sessionId => _plugin.PartyParticipants.TryGetSession(
                    party.Id,
                    sessionId,
                    out _),
                DateTime.UtcNow);
        }

        public async Task<PartyManualSynchronizationResult> SynchronizePartyNowAsync(
            string partyId,
            IEnumerable<string> requestedSessionIds)
        {
            var selectedSessionIds = new HashSet<string>(
                (requestedSessionIds ?? Array.Empty<string>())
                    .Where(sessionId => !string.IsNullOrWhiteSpace(sessionId)),
                StringComparer.Ordinal);
            if (selectedSessionIds.Count == 0)
            {
                return new PartyManualSynchronizationResult
                {
                    Accepted = false,
                    Message = "请至少选择一台在线客户端"
                };
            }

            WatchPartyItem party;
            lock (_plugin.ConfigurationSyncRoot)
            {
                party = _plugin.Configuration.WatchParties.FirstOrDefault(candidate =>
                    candidate.IsActive
                    && string.Equals(candidate.Id, partyId, StringComparison.Ordinal));
            }

            if (party == null)
            {
                return new PartyManualSynchronizationResult
                {
                    Accepted = false,
                    Message = "房间不存在或尚未启用"
                };
            }

            var targetEpisode = party.IsSeriesParty
                ? SeriesPartyQueue.GetCurrentEpisode(party)
                : null;
            var targetItemId = targetEpisode?.ItemId ?? party.ItemId;
            var targetItem = string.IsNullOrEmpty(targetItemId)
                ? null
                : _libraryManager.GetItemById(targetItemId);
            if (targetItem == null)
            {
                return new PartyManualSynchronizationResult
                {
                    Accepted = false,
                    Message = "房间当前媒体已不在 Emby 媒体库中"
                };
            }

            var masterSessionId = GetActiveMasterSession(party)?.Id;
            var activeSessions = SessionInfoIndex.Build(_sessionManager.Sessions);
            var nowUtc = DateTime.UtcNow;
            var positionTicks = _playbackSyncCoordinator.GetEstimatedPartyPosition(
                party.Id,
                party.CurrentPositionTicks,
                nowUtc);
            // A manual sync is also the explicit join action. Discover every current
            // online Emby session instead of requiring a prior PlaybackStart/Progress
            // report, then register eligible users before dispatching PlayNow.
            var targetSessions = PartySessionDiscovery.SelectRequested(
                activeSessions.Values,
                nowUtc,
                selectedSessionIds,
                session => PartyLaunchTargetProjector.BuildEvaluatedTarget(
                    session,
                    masterSessionId,
                    party.MasterUserId,
                    CanUserJoinParty(party, session.UserId),
                    _plugin.PartyParticipants.TryGetSession(
                        party.Id,
                        session.Id,
                        out _),
                    nowUtc).CanLaunch);
            var targets = new List<PartyManualSynchronizationTarget>();
            var selectedMasterSessionId = masterSessionId;
            foreach (var session in targetSessions)
            {
                if (GetOrCreateParticipant(
                        party.Id,
                        session,
                        maxParticipants: party.MaxParticipants) == null)
                {
                    continue;
                }

                if (string.IsNullOrEmpty(selectedMasterSessionId)
                    && string.Equals(
                        session.UserId,
                        party.MasterUserId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (_plugin.PartyParticipants.SetMasterSession(
                            party.Id,
                            session.Id))
                    {
                        selectedMasterSessionId = session.Id;
                    }
                }

                targets.Add(new PartyManualSynchronizationTarget
                {
                    SessionId = session.Id,
                    UserName = session.UserName ?? session.UserId,
                    Client = session.Client ?? string.Empty,
                    IsOnline = true,
                    SupportsRemotePlayback = SupportsRemoteControlledPlayback(session),
                    IsDormant = _participantDormancies.IsDormant(party.Id, session.Id)
                });
            }
            var masterSession = GetMasterSessionForCommand(party);
            var request = new PlayRequest
            {
                ItemIds = new[] { targetItem.InternalId },
                MediaSourceId = targetEpisode?.MediaSourceId ?? party.MediaSourceId,
                PlayCommand = PlayCommand.PlayNow,
                StartPositionTicks = Math.Max(0, positionTicks)
            };

            var wasWaitingRoom = party.IsWaitingRoom;
            var wasPlaying = party.IsPlaying;
            if (wasWaitingRoom || !wasPlaying)
            {
                // The button is an explicit override of the waiting-room gate: its
                // contract is to start the selected media now, not merely mark users
                // ready for a later Start action.
                party.IsWaitingRoom = false;
                party.IsPlaying = true;
                _playbackSyncCoordinator.UpdateMasterPosition(
                    party.Id,
                    positionTicks,
                    true,
                    nowUtc,
                    TimeSpan.FromSeconds(party.SyncToleranceSeconds).Ticks);
                _plugin.SaveConfigurationSafely();
            }

            var result = await _manualSynchronizationCoordinator.SynchronizeAsync(
                targets,
                new PartyManualSynchronizationOperations
                {
                    EnsureControlConnectionAsync = (target, cancellationToken) =>
                        WaitForOfficialIosWebSocketAsync(
                            target.SessionId,
                            ParticipantRoomCommand.PlayNow,
                            cancellationToken),
                    DispatchAsync = (target, cancellationToken) =>
                        SendPlayCommandSerialAsync(
                            party.Id,
                            masterSession?.Id ?? target.SessionId,
                            target.SessionId,
                            request,
                            cancellationToken,
                            allowDormant: target.IsDormant),
                    HasControlConnection = target =>
                        HasPlaybackControlConnection(target.SessionId),
                    OnDispatchError = (target, ex) => _logger.ErrorException(
                        $"[Party {party.Id}] Manual synchronization failed for " +
                        $"session {target.SessionId}",
                        ex)
                },
                _lifetimeCts.Token).ConfigureAwait(false);

            if (!result.Accepted && (wasWaitingRoom || !wasPlaying))
            {
                party.IsWaitingRoom = wasWaitingRoom;
                party.IsPlaying = wasPlaying;
                if (!wasPlaying)
                {
                    _playbackSyncCoordinator.StopMasterClock(
                        party.Id,
                        positionTicks,
                        DateTime.UtcNow);
                }
                _plugin.SaveConfigurationSafely();
            }

            return result;
        }

        private bool HasPlaybackControlConnection(string sessionId)
        {
            var session = _sessionManager.Sessions.FirstOrDefault(candidate =>
                candidate != null
                && string.Equals(candidate.Id, sessionId, StringComparison.Ordinal));
            return session != null
                && PartySessionLivenessPolicy.IsOnline(session, DateTime.UtcNow)
                && _officialIosWebSocketTransport.CanDispatchPlaybackCommand(
                    session.Client,
                    session.SessionControllers);
        }

        public ServerEntryPoint(
            ISessionManager sessionManager,
            ILibraryManager libraryManager,
            ILogManager logManager)
        {
            _sessionManager = sessionManager;
            _libraryManager = libraryManager;
            _logger = logManager.GetLogger(GetType().Name);
            _plugin = Plugin.Instance;
            _mediaIdentityResolver = new WatchPartyMediaIdentityResolver(
                _libraryManager,
                _logger);
            _playbackCommandQueue = new DormancyAwarePlaybackCommandQueue(
                _participantDormancies,
                capacityPerSession: 256);
            _partyPlaybackTransitions = new PartyPlaybackTransitionCoordinator(
                _lifetimeCts.Token);
            Current = this;
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

            _mediaIdentityResolver.ClearConfiguredItemCache();
            NormalizePartyConfiguration();
            var config = _plugin.Configuration;
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

        private void UpdateParticipantActivity(
            string partyId,
            string sessionId,
            long positionTicks,
            bool isPaused,
            string playSessionId = null)
        {
            if (string.IsNullOrEmpty(playSessionId))
            {
                _plugin.PartyParticipants.UpdateActivity(
                    partyId,
                    sessionId,
                    positionTicks,
                    isPaused,
                    DateTime.UtcNow);
                return;
            }

            _plugin.PartyParticipants.UpdateActivity(
                partyId,
                sessionId,
                playSessionId,
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
            _participantDormancies.Cancel(partyId, sessionId);
            _participantResumeCoordinator.CancelSession(sessionId);
            _playbackSyncCoordinator.ClearSession(sessionId);
            _participantResumeLatencies.ClearSession(sessionId);
            _seriesEpisodeTransitions.ClearSession(sessionId);
            ClearSessionEpisodeMarkers(partyId, sessionId);
            _masterPlaybackStateDebouncer.Cancel(sessionId);
            CancelPendingMasterPauseCommit(partyId);
            if (!_plugin.PartyParticipants.HasUser(partyId, participant.UserId))
            {
                _plugin.PartyReadyUsers.RemoveUser(partyId, participant.UserId);
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
                            && IsRegisteredPartyPlayback(party, candidate))))
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
            var nowUtc = DateTime.UtcNow;
            return string.IsNullOrEmpty(masterSessionId)
                ? null
                : _sessionManager.Sessions.FirstOrDefault(s =>
                    string.Equals(s.Id, masterSessionId, StringComparison.Ordinal)
                    && PartySessionLivenessPolicy.IsOnline(s, nowUtc)
                    && IsRegisteredPartyPlayback(party, s));
        }

        private bool IsRegisteredPartyPlayback(WatchPartyItem party, SessionInfo session)
        {
            if (party == null || session == null)
            {
                return false;
            }

            // Emby's SessionInfo is shared by every PlaySessionId in one browser
            // session. A zombie reporter from an older episode can overwrite
            // NowPlayingItem/PlayState even though the plugin correctly rejected that
            // report. The registry is the playback-generation boundary, while the
            // live item still has to belong to this party. For a series, any queued
            // episode is accepted here; requiring the current episode would reintroduce
            // the stale-old-episode takeover bug.
            if (!_plugin.PartyParticipants.TryGetSession(
                    party.Id,
                    session.Id,
                    out var participant)
                || !_participantDormancies.CanReceiveCommand(
                    party.Id,
                    session.Id,
                    ParticipantRoomCommand.PlayNow)
                || string.IsNullOrEmpty(participant.PlaySessionId)
                || !string.Equals(
                    participant.UserId,
                    session.UserId,
                    StringComparison.OrdinalIgnoreCase)
                || session.NowPlayingItem == null)
            {
                return false;
            }

            var item = _libraryManager.GetItemById(session.NowPlayingItem.Id);
            var matchingParty = FindPartyForItem(item);
            return matchingParty != null
                && string.Equals(matchingParty.Id, party.Id, StringComparison.Ordinal);
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
                    && IsRegisteredPartyPlayback(party, current)));
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
            return CanUserJoinParty(party, userId, logDenied: true);
        }

        private bool CanUserJoinParty(
            WatchPartyItem party,
            string userId,
            bool logDenied)
        {
            if (party.AllowedUserIds != null && party.AllowedUserIds.Count > 0)
            {
                if (!party.AllowedUserIds.Any(allowedUserId =>
                        string.Equals(
                            allowedUserId,
                            userId,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    if (logDenied)
                    {
                        _logger.Warn($"[Party {party.Id}] User {userId} not in allowed list");
                    }
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
                if (logDenied)
                {
                    _logger.Warn($"[Party {party.Id}] Maximum participants ({party.MaxParticipants}) reached");
                }
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
            var activeMasterSessionId = _plugin.PartyParticipants.GetMasterSession(party.Id);
            var hasActiveMaster = HasActiveMasterSession(party);
            var inactiveSessions = _plugin.PartyParticipants.GetSessions(party.Id)
                .Where(participant => participant.LastActivityAt < inactiveThreshold)
                // A participant can disappear from Emby's live session list while its
                // iOS app is backgrounded. As long as the master clock is alive, keep
                // that membership and let the returning progress packet reattach it;
                // inactive cleanup resumes once the master has actually left.
                .Where(participant => !hasActiveMaster
                    || string.Equals(
                        participant.SessionId,
                        activeMasterSessionId,
                        StringComparison.Ordinal))
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
            // A promoted/new master may use a different browser session. Let that
            // session use the progress-jump fallback until its explicit seek endpoint
            // has been observed once.
            _masterSeekSources.ClearParty(party.Id);
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

            var sessions = GetRegisteredPartySessions(
                party.Id,
                ParticipantRoomCommand.Resume);
            foreach (var session in sessions)
            {
                if (!_plugin.PartyParticipants.TryGetSession(party.Id, session.Id, out _)
                    || !SupportsRemoteControlledPlayback(session))
                {
                    continue;
                }

                var item = session.NowPlayingItem == null
                    ? null
                    : _libraryManager.GetItemById(session.NowPlayingItem.Id);
                var matchingParty = FindPartyForItem(item);
                if (item != null && (matchingParty == null || matchingParty.Id != party.Id))
                {
                    continue;
                }

                try
                {
                    if (await SendPauseStateCommand(party, session, false))
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

        private Task HandleMasterPause(
            WatchPartyItem party,
            SessionInfo session,
            long? reportedPositionTicks)
        {
            ApplyAuthoritativePauseState(
                party,
                isPaused: true,
                reportedPositionTicks);
            CancelParticipantResumePipelines(party.Id);
            var transition = _partyPlaybackTransitions.Begin(party.Id);
            _logger.Info(
                $"[Party {party.Id}] Master {session.UserName} paused; " +
                "broadcasting authoritative pause");
            var pauseTask = Task.WhenAll(
                PauseAllUsers(party, session.Id, transition.Token),
                SyncParticipantsAfterPause(
                    party,
                    session,
                    reportedPositionTicks,
                    transition.Token));
            _ = ObserveParticipantPauseCompletionAsync(
                pauseTask,
                party.Id,
                transition);
            return Task.CompletedTask;
        }

        private void ApplyAuthoritativePauseState(
            WatchPartyItem party,
            bool isPaused,
            long? reportedPositionTicks = null)
        {
            if (party == null)
            {
                return;
            }

            var nowUtc = DateTime.UtcNow;
            var fallbackPosition = reportedPositionTicks.HasValue
                ? Math.Max(0, reportedPositionTicks.Value)
                : party.CurrentPositionTicks;
            var authoritativePosition = _playbackSyncCoordinator.SetMasterPlaybackState(
                party.Id,
                isPlaying: !isPaused,
                fallbackPosition,
                nowUtc);
            party.CurrentPositionTicks = authoritativePosition;
            party.IsPlaying = !isPaused;
        }

        private async Task SyncParticipantsAfterPause(
            WatchPartyItem party,
            SessionInfo masterSession,
            long? reportedPositionTicks,
            CancellationToken cancellationToken)
        {
            // A drag immediately before pause may still have a debounced sync waiting to
            // fire. The explicit pause sync supersedes it and must be the only final seek.
            CancelPendingMasterSeekSync(party.Id);

            var controllingSession = GetMasterSessionForCommand(party);
            if (controllingSession == null)
            {
                _logger.Info($"[Party {party.Id}] Cannot run pause position sync because no active master session is available");
                return;
            }

            var targetPosition = reportedPositionTicks.HasValue
                ? Math.Max(0, reportedPositionTicks.Value)
                : _playbackSyncCoordinator.GetEstimatedPartyPosition(
                    party.Id,
                    party.CurrentPositionTicks,
                    DateTime.UtcNow);

            _logger.Info(
                $"[Party {party.Id}] Pause accepted from master {masterSession.UserName}; " +
                $"actively syncing participants to master position " +
                $"{TimeSpan.FromTicks(targetPosition).TotalSeconds:F1}s");

            var sessions = GetRegisteredPartySessions(
                party.Id,
                ParticipantRoomCommand.Seek);
            var syncTasks = new List<Task>();
            foreach (var session in sessions)
            {
                // The registered master defines the pause position and is never seeked
                // back to a participant's clock.
                if (IsMasterSession(party, session))
                {
                    continue;
                }

                var item = session.NowPlayingItem == null
                    ? null
                    : _libraryManager.GetItemById(session.NowPlayingItem.Id);
                var matchingParty = FindPartyForItem(item);
                if (item != null && (matchingParty == null || matchingParty.Id != party.Id))
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

                syncTasks.Add(SyncUserToPosition(
                    party,
                    session,
                    item,
                    targetPosition,
                    allowReplace: true,
                    controllingSession: controllingSession,
                    applySyncOffset: false,
                    force: true,
                    cancellationToken: cancellationToken));
            }

            await Task.WhenAll(syncTasks).ConfigureAwait(false);
        }

        private async Task ResumeParticipantsWithCompensation(
            WatchPartyItem party,
            string masterSessionId)
        {
            // Each SessionId owns an independent Resume -> compensation Seek pipeline.
            // A slow DMIT/IPv4 client therefore cannot hold back an IPv6/direct client,
            // while the target is projected only after that participant's Resume work
            // finishes. Periodic calibration remains the long-term guard.
            CancelPendingMasterSeekSync(party.Id);
            var sessions = GetRegisteredPartySessions(
                party.Id,
                ParticipantRoomCommand.Resume);
            var resumeTasks = new List<Task>();
            foreach (var participantSession in sessions)
            {
                if (IsMasterSession(party, participantSession)
                    || participantSession.Id == masterSessionId)
                {
                    continue;
                }

                var item = participantSession.NowPlayingItem == null
                    ? null
                    : _libraryManager.GetItemById(participantSession.NowPlayingItem.Id);
                var matchingParty = FindPartyForItem(item);
                if ((item == null || (matchingParty != null && matchingParty.Id == party.Id))
                    && _plugin.PartyParticipants.TryGetSession(
                        party.Id,
                        participantSession.Id,
                        out _))
                {
                    resumeTasks.Add(ResumeParticipantWithCompensation(
                        party,
                        participantSession));
                }
            }

            await Task.WhenAll(resumeTasks).ConfigureAwait(false);
        }

        private async Task ResumeParticipantWithCompensation(
            WatchPartyItem party,
            SessionInfo participantSession)
        {
            try
            {
                Func<long> getTargetPositionTicks = () =>
                {
                    var nowUtc = DateTime.UtcNow;
                    var estimatedPosition =
                        _playbackSyncCoordinator.GetEstimatedPartyPosition(
                            party.Id,
                            party.CurrentPositionTicks,
                            nowUtc);
                    var configuredOffset = TimeSpan.FromMilliseconds(
                        _plugin.Configuration.SyncOffsetMilliseconds);
                    var learnedLatency =
                        _participantResumeLatencies.GetEstimatedLatency(
                            participantSession.Id);
                    var targetPosition = Math.Max(
                        0,
                        estimatedPosition
                            + configuredOffset.Ticks
                            + learnedLatency.Ticks);

                    _logger.Info(
                        $"[Party {party.Id}] Resume compensation for session " +
                        $"{participantSession.Id}: target " +
                        $"{TimeSpan.FromTicks(targetPosition).TotalSeconds:F1}s " +
                        $"(clock {TimeSpan.FromTicks(estimatedPosition).TotalSeconds:F1}s, " +
                        $"configured {configuredOffset.TotalMilliseconds:F0}ms, " +
                        $"learned {learnedLatency.TotalMilliseconds:F0}ms)");
                    return targetPosition;
                };

                var resumeSeekConfirmed = await _participantResumeCoordinator.ResumeAsync(
                    participantSession.Id,
                    queuedToken => CanContinueParticipantResume(
                            party,
                            participantSession.Id)
                        ? SendPauseStateCommand(
                            party,
                            participantSession,
                            isPaused: false,
                            cancellationToken: queuedToken)
                        : Task.FromResult(false),
                    getTargetPositionTicks,
                    async (targetPosition, queuedToken) =>
                    {
                        return await _confirmedPlaybackSeekRetrier.SendAsync(
                            participantSession.Id,
                            targetPosition,
                            getTargetPositionTicks,
                            async (confirmedTargetPosition, confirmationToken) =>
                            {
                                if (!CanContinueParticipantResume(
                                        party,
                                        participantSession.Id))
                                {
                                    return false;
                                }

                                var currentItem = participantSession.NowPlayingItem == null
                                    ? null
                                    : _libraryManager.GetItemById(
                                        participantSession.NowPlayingItem.Id);
                                var matchingParty = FindPartyForItem(currentItem);
                                if (currentItem != null
                                    && (matchingParty == null
                                        || matchingParty.Id != party.Id))
                                {
                                    return false;
                                }

                                return await SyncUserToPosition(
                                    party,
                                    participantSession,
                                    currentItem,
                                    confirmedTargetPosition,
                                    allowReplace: true,
                                    controllingSession: GetMasterSessionForCommand(party),
                                    applySyncOffset: false,
                                    cancellationToken: confirmationToken)
                                    .ConfigureAwait(false);
                            },
                            queuedToken).ConfigureAwait(false);
                    },
                    _lifetimeCts.Token).ConfigureAwait(false);
                if (!resumeSeekConfirmed
                    && CanContinueParticipantResume(
                        party,
                        participantSession.Id))
                {
                    _logger.Warn(
                        $"[Party {party.Id}] Resume seek was not confirmed for session " +
                        $"{participantSession.Id} after {ResumeSeekMaxAttempts} attempts");
                }
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
            {
                // Plugin shutdown cancels pending participant pipelines.
            }
            catch (OperationCanceledException)
            {
                _logger.Debug(
                    $"[Party {party.Id}] Resume compensation was superseded for " +
                    $"session {participantSession.Id}");
            }
            catch (Exception ex)
            {
                _logger.ErrorException(
                    $"[Party {party.Id}] Error resuming session " +
                    $"{participantSession.Id} with latency compensation",
                    ex);
            }
        }

        private async Task<bool> ReconcileParticipantAfterReconnect(
            WatchPartyItem party,
            SessionInfo participantSession,
            BaseItem item,
            long currentPosition,
            long targetPosition)
        {
            if (party == null || participantSession == null)
            {
                return false;
            }

            var isMasterPaused = !party.IsPlaying;
            var drift = Math.Abs(currentPosition - targetPosition);
            var controllingSession = GetMasterSessionForCommand(party);
            var positionReconciled = true;

            _logger.Info(
                $"[Party {party.Id}] Reconnecting session {participantSession.Id}: " +
                $"master is {(isMasterPaused ? "paused" : "playing")}, " +
                $"participant at {TimeSpan.FromTicks(currentPosition).TotalSeconds:F1}s, " +
                $"master at {TimeSpan.FromTicks(targetPosition).TotalSeconds:F1}s");

            // Pause before seeking a paused party. For a playing party, seek first and
            // unpause afterwards so the final command state is authoritative even when
            // the native client rebuilt its player while reconnecting.
            if (isMasterPaused)
            {
                await SendPauseStateCommand(party, participantSession, isPaused: true);
            }

            if (drift > TimeSpan.FromSeconds(2).Ticks)
            {
                var episodeSwitchTarget = GetSeriesEpisodeSwitchTarget(party, item);
                if (episodeSwitchTarget != null)
                {
                    await PlaySeriesEpisodeForSessions(
                        party,
                        episodeSwitchTarget,
                        new[] { participantSession },
                        targetPosition);
                }
                else
                {
                    positionReconciled = await SyncUserToPosition(
                        party,
                        participantSession,
                        item,
                        targetPosition,
                        allowReplace: true,
                        controllingSession: controllingSession,
                        force: true);
                }
            }
            else
            {
                _logger.Debug(
                    $"[Party {party.Id}] Reconnected session {participantSession.Id} " +
                    $"within {TimeSpan.FromTicks(drift).TotalSeconds:F1}s; skipping seek");
            }

            if (!isMasterPaused)
            {
                await SendPauseStateCommand(party, participantSession, isPaused: false);
            }

            return positionReconciled;
        }

        private async Task PauseAllUsers(
            WatchPartyItem party,
            string excludeSessionId,
            CancellationToken cancellationToken = default)
        {
            await BroadcastPauseState(
                party,
                excludeSessionId,
                isPaused: true,
                cancellationToken);
        }

        private bool CanContinueParticipantResume(
            WatchPartyItem party,
            string sessionId)
        {
            return party != null
                && party.IsActive
                && party.IsPlaying
                && !party.IsWaitingRoom
                && _plugin.PartyParticipants.TryGetSession(
                    party.Id,
                    sessionId,
                    out _)
                && _participantDormancies.CanReceiveCommand(
                    party.Id,
                    sessionId,
                    ParticipantRoomCommand.Resume);
        }

        private void CancelParticipantResumePipelines(string partyId)
        {
            foreach (var participant in _plugin.PartyParticipants.GetSessions(partyId))
            {
                _participantResumeCoordinator.CancelSession(
                    participant.SessionId);
                _participantResumeLatencies.CancelPendingResume(
                    participant.SessionId);
            }
        }

        private Task HandleMasterResume(WatchPartyItem party, SessionInfo session)
        {
            _partyPlaybackTransitions.Cancel(party.Id);
            ApplyAuthoritativePauseState(
                party,
                isPaused: false);
            _logger.Info(
                $"[Party {party.Id}] Master {session.UserName} resumed; " +
                "broadcasting authoritative play state");
            if (!party.IsWaitingRoom)
            {
                // Confirmation waits must not hold the master's serialized event queue.
                // A following Pause needs to update authority and cancel these pipelines
                // immediately, before an unconfirmed compensation Seek can retry.
                var resumeTask = ResumeParticipantsWithCompensation(
                    party,
                    session.Id);
                _ = ObserveParticipantCommandCompletionAsync(
                    resumeTask,
                    party.Id,
                    "resume synchronization");
            }

            return Task.CompletedTask;
        }

        private async Task ObserveParticipantCommandCompletionAsync(
            Task resumeTask,
            string partyId,
            string operation)
        {
            try
            {
                await resumeTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
            {
                // Plugin shutdown cancels pending participant pipelines.
            }
            catch (Exception ex)
            {
                _logger.ErrorException(
                    $"[Party {partyId}] Background participant {operation} failed",
                    ex);
            }
        }

        private async Task ObserveParticipantPauseCompletionAsync(
            Task pauseTask,
            string partyId,
            PartyPlaybackTransitionRegistration transition)
        {
            try
            {
                await pauseTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (transition.Token.IsCancellationRequested)
            {
                _logger.Debug(
                    $"[Party {partyId}] Superseded participant pause synchronization cancelled");
            }
            catch (Exception ex)
            {
                _logger.ErrorException(
                    $"[Party {partyId}] Background participant pause synchronization failed",
                    ex);
            }
            finally
            {
                _partyPlaybackTransitions.Complete(transition);
            }
        }

        private async Task RestoreParticipantPlaybackState(
            WatchPartyItem party,
            SessionInfo session)
        {
            // The master may disconnect after the progress event was classified but
            // before this asynchronous restore is dispatched. A participant is free
            // whenever there is no live master clock; the room's persisted IsPlaying
            // value is only history in that state, not authority.
            if (!HasActiveMasterSession(party))
            {
                _logger.Debug(
                    $"[Party {party.Id}] Leaving participant {session.Id} playback state unchanged " +
                    "because the master is offline");
                return;
            }

            var authoritativeIsPaused = party.IsWaitingRoom || !party.IsPlaying;
            _logger.Info(
                $"[Party {party.Id}] Ignoring playback-state change from participant " +
                $"{session.UserName}; restoring master " +
                $"{(authoritativeIsPaused ? "pause" : "play")} state");
            await SendPauseStateCommand(party, session, authoritativeIsPaused);
        }

        private async Task BroadcastPauseState(
            WatchPartyItem party,
            string excludeSessionId,
            bool isPaused,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var sessions = GetRegisteredPartySessions(
                    party.Id,
                    isPaused
                        ? ParticipantRoomCommand.Pause
                        : ParticipantRoomCommand.Resume);

                var sendTasks = new List<Task>();
                foreach (var otherSession in sessions)
                {
                    if (otherSession.Id == excludeSessionId)
                    {
                        continue;
                    }

                    var item = otherSession.NowPlayingItem == null
                        ? null
                        : _libraryManager.GetItemById(otherSession.NowPlayingItem.Id);
                    var matchingParty = FindPartyForItem(item);

                    if ((item == null || (matchingParty != null && matchingParty.Id == party.Id))
                        && _plugin.PartyParticipants.TryGetSession(
                            party.Id,
                            otherSession.Id,
                            out _))
                    {
                        var commandName = isPaused ? "Pausing" : "Resuming";
                        _logger.Info(
                            $"[Party {party.Id}] {commandName} user {otherSession.UserName} " +
                            $"(Session: {otherSession.Id})");
                        sendTasks.Add(SendPauseStateCommandSafely(
                            party,
                            otherSession,
                            isPaused,
                            cancellationToken));
                    }
                }

                await Task.WhenAll(sendTasks).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.ErrorException(
                    $"[Party {party.Id}] Error broadcasting {(isPaused ? "pause" : "resume")}",
                    ex);
            }
        }

        private async Task SendPauseStateCommandSafely(
            WatchPartyItem party,
            SessionInfo session,
            bool isPaused,
            CancellationToken cancellationToken)
        {
            try
            {
                await SendPauseStateCommand(
                    party,
                    session,
                    isPaused,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.ErrorException(
                    $"[Party {party.Id}] Error sending {(isPaused ? "pause" : "resume")} " +
                    $"to user {session.UserName}",
                    ex);
            }
        }

        private async Task StopParticipantsAfterMasterStop(
            WatchPartyItem party,
            string stoppedMasterSessionId)
        {
            CancelParticipantResumePipelines(party.Id);
            var sessions = GetRegisteredPartySessions(
                party.Id,
                ParticipantRoomCommand.Stop);
            foreach (var session in sessions)
            {
                if (session == null
                    || string.Equals(
                        session.Id,
                        stoppedMasterSessionId,
                        StringComparison.Ordinal)
                    || !_plugin.PartyParticipants.TryGetSession(
                        party.Id,
                        session.Id,
                        out _))
                {
                    continue;
                }

                if (!SupportsRemoteControlledPlayback(session))
                {
                    _logger.Info(
                        $"[Party {party.Id}] Cannot mirror master Stop to " +
                        $"{session.UserName} ({session.Client}): remote playback is unsupported");
                    continue;
                }

                try
                {
                    await SendPlaystateCommandSerialAsync(
                        party.Id,
                        stoppedMasterSessionId,
                        session.Id,
                        new PlaystateRequest { Command = PlaystateCommand.Stop });
                    _logger.Info(
                        $"[Party {party.Id}] Mirrored master Stop to {session.UserName} " +
                        $"(Session: {session.Id})");
                }
                catch (Exception ex)
                {
                    _logger.ErrorException(
                        $"[Party {party.Id}] Error mirroring master Stop to session {session.Id}",
                        ex);
                }
            }
        }

        private async Task<bool> SendPauseStateCommand(
            WatchPartyItem party,
            SessionInfo session,
            bool isPaused,
            CancellationToken cancellationToken = default)
        {
            if (session == null
                || !SupportsRemoteControlledPlayback(session))
            {
                _logger.Info(
                    $"[Watch Party] Session {session?.Id} ({session?.Client}) does not " +
                    $"advertise pause control, skipping {(isPaused ? "pause" : "unpause")}");
                return false;
            }

            if (isPaused)
            {
                _participantResumeCoordinator.CancelSession(session.Id);
                _participantResumeLatencies.CancelPendingResume(session.Id);
            }

            var commandToken = cancellationToken.CanBeCanceled
                ? cancellationToken
                : _lifetimeCts.Token;

            var expectation = _playbackSyncCoordinator.ExpectPauseState(
                session.Id,
                isPaused,
                DateTime.UtcNow);
            var commandDispatched = false;
            try
            {
                var request = new PlaystateRequest
                {
                    Command = isPaused ? PlaystateCommand.Pause : PlaystateCommand.Unpause
                };
                var resumeObservationStarted = false;

                var commandSent = await _playbackStateCommandRetrier.SendAsync(
                    async queuedToken =>
                    {
                        Action<DateTime> onDispatching = dispatchedAtUtc =>
                        {
                            commandDispatched = true;
                            if (!isPaused && !resumeObservationStarted)
                            {
                                _participantResumeLatencies.RecordResumeCommand(
                                    session.Id,
                                    expectation,
                                    dispatchedAtUtc);
                                resumeObservationStarted = true;
                            }
                        };

                        return await SendPlaystateCommandSerialAsync(
                            party?.Id,
                            session.Id,
                            session.Id,
                            request,
                            queuedToken,
                            onDispatching).ConfigureAwait(false);
                    },
                    () => _playbackSyncCoordinator.IsPauseStateExpectationPending(
                        session.Id,
                        expectation,
                        DateTime.UtcNow),
                    (attempt, ex) =>
                        _logger.Warn(
                            $"[Watch Party] {(isPaused ? "pause" : "resume")} command " +
                            $"attempt {attempt}/{PauseCommandMaxAttempts} failed for " +
                            $"session {session.Id}: {ex.Message}; retrying"),
                    nextAttempt => _logger.Debug(
                        $"[Watch Party] No {(isPaused ? "pause" : "resume")} echo yet for " +
                        $"session {session.Id}; waiting before retry " +
                        $"({nextAttempt}/{PauseCommandMaxAttempts})"),
                    commandToken).ConfigureAwait(false);
                if (!commandSent)
                {
                    if (!isPaused)
                    {
                        _participantResumeLatencies.CancelPendingResume(
                            session.Id,
                            expectation);
                    }
                    _playbackSyncCoordinator.CompletePauseStateCommandAttempt(
                        session.Id,
                        expectation,
                        commandDispatched);
                    return false;
                }

                return true;
            }
            catch (OperationCanceledException) when (commandToken.IsCancellationRequested)
            {
                if (!isPaused)
                {
                    _participantResumeLatencies.CancelPendingResume(
                        session.Id,
                        expectation);
                }
                _playbackSyncCoordinator.CompletePauseStateCommandAttempt(
                    session.Id,
                    expectation,
                    commandDispatched);
                return false;
            }
            catch
            {
                if (!isPaused)
                {
                    _participantResumeLatencies.CancelPendingResume(
                        session.Id,
                        expectation);
                }
                _playbackSyncCoordinator.CompletePauseStateCommandAttempt(
                    session.Id,
                    expectation,
                    commandDispatched);
                throw;
            }
        }

        private async Task<bool> SendPlaystateCommandSerialAsync(
            string partyId,
            string controllingSessionId,
            string targetSessionId,
            PlaystateRequest request,
            CancellationToken cancellationToken = default,
            Action<DateTime> onDispatching = null)
        {
            if (string.IsNullOrEmpty(targetSessionId) || request == null)
            {
                return false;
            }

            var command = ToParticipantRoomCommand(request.Command);
            if (command == ParticipantRoomCommand.Stop)
            {
                _participantResumeCoordinator.CancelSession(targetSessionId);
                _participantResumeLatencies.CancelPendingResume(targetSessionId);
            }
            if (!CanDispatchParticipantCommand(
                    partyId,
                    targetSessionId,
                    command,
                    queued: false))
            {
                return false;
            }
            var commandToken = cancellationToken.CanBeCanceled
                ? cancellationToken
                : _lifetimeCts.Token;
            var transportAvailable = false;
            var commandSent = await _playbackCommandQueue.EnqueueAsync(
                partyId,
                targetSessionId,
                command,
                request.Command == PlaystateCommand.Seek
                    ? ParticipantCommandQueueMode.Latest
                    : ParticipantCommandQueueMode.Ordered,
                async queuedToken =>
                {
                    transportAvailable = await WaitForOfficialIosWebSocketAsync(
                        targetSessionId,
                        command,
                        queuedToken).ConfigureAwait(false);
                    if (!transportAvailable)
                    {
                        return;
                    }
                    onDispatching?.Invoke(DateTime.UtcNow);
                    await _sessionManager.SendPlaystateCommand(
                        controllingSessionId,
                        targetSessionId,
                        request,
                        queuedToken).ConfigureAwait(false);
                },
                commandToken).ConfigureAwait(false);
            commandSent = commandSent && transportAvailable;
            if (!commandSent)
            {
                CanDispatchParticipantCommand(
                    partyId,
                    targetSessionId,
                    command,
                    queued: true);
            }
            return commandSent;
        }

        private static ParticipantRoomCommand ToParticipantRoomCommand(
            PlaystateCommand command)
        {
            switch (command)
            {
                case PlaystateCommand.Pause:
                    return ParticipantRoomCommand.Pause;
                case PlaystateCommand.Unpause:
                    return ParticipantRoomCommand.Resume;
                case PlaystateCommand.Seek:
                    return ParticipantRoomCommand.Seek;
                case PlaystateCommand.Stop:
                    return ParticipantRoomCommand.Stop;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(command),
                        command,
                        "Unsupported participant playstate command");
            }
        }

        private async Task<bool> SendPlayCommandSerialAsync(
            string partyId,
            string controllingSessionId,
            string targetSessionId,
            PlayRequest request,
            CancellationToken cancellationToken,
            bool allowDormant = false)
        {
            if (string.IsNullOrEmpty(targetSessionId) || request == null)
            {
                return false;
            }

            if (!allowDormant && !CanDispatchParticipantCommand(
                    partyId,
                    targetSessionId,
                    ParticipantRoomCommand.PlayNow,
                    queued: false))
            {
                return false;
            }
            var commandToken = cancellationToken.CanBeCanceled
                ? cancellationToken
                : _lifetimeCts.Token;
            var transportAvailable = false;
            Func<CancellationToken, Task> dispatch = async queuedToken =>
            {
                transportAvailable = await WaitForOfficialIosWebSocketAsync(
                    targetSessionId,
                    ParticipantRoomCommand.PlayNow,
                    queuedToken).ConfigureAwait(false);
                if (!transportAvailable)
                {
                    return;
                }
                await _sessionManager.SendPlayCommand(
                    controllingSessionId,
                    targetSessionId,
                    request,
                    queuedToken).ConfigureAwait(false);
            };
            var commandSent = allowDormant
                ? await _playbackCommandQueue.EnqueueExplicitPlayNowAsync(
                    partyId,
                    targetSessionId,
                    dispatch,
                    commandToken).ConfigureAwait(false)
                : await _playbackCommandQueue.EnqueueAsync(
                    partyId,
                    targetSessionId,
                    ParticipantRoomCommand.PlayNow,
                    ParticipantCommandQueueMode.Ordered,
                    dispatch,
                    commandToken).ConfigureAwait(false);
            commandSent = commandSent && transportAvailable;
            if (!commandSent && !allowDormant)
            {
                CanDispatchParticipantCommand(
                    partyId,
                    targetSessionId,
                    ParticipantRoomCommand.PlayNow,
                    queued: true);
            }
            return commandSent;
        }

        private async Task<bool> WaitForOfficialIosWebSocketAsync(
            string targetSessionId,
            ParticipantRoomCommand command,
            CancellationToken cancellationToken)
        {
            var targetSession = _sessionManager.Sessions.FirstOrDefault(session =>
                session != null
                && string.Equals(session.Id, targetSessionId, StringComparison.Ordinal));
            if (targetSession == null)
            {
                return false;
            }
            if (!OfficialIosWebSocketTransport.IsOfficialIosClient(targetSession.Client))
            {
                return PartySessionLivenessPolicy.IsOnline(
                    targetSession,
                    DateTime.UtcNow);
            }

            var available = await _officialIosWebSocketTransport
                .WaitForPlaybackCommandTransportAsync(
                    targetSession.Client,
                    () => _sessionManager.Sessions
                        .FirstOrDefault(session =>
                            session != null
                            && string.Equals(session.Id, targetSessionId, StringComparison.Ordinal))
                        ?.SessionControllers,
                    OfficialIosWebSocketRecoveryTimeout,
                    OfficialIosWebSocketRecoveryPollInterval,
                    cancellationToken)
                .ConfigureAwait(false);
            if (available)
            {
                return true;
            }

            _logger.Info(
                $"[Watch Party] Skipping {command} for Emby for iOS session " +
                $"{targetSessionId} because no active WebSocket controller is available; " +
                "Firebase fallback is disabled for Watch Party");
            return false;
        }

        private bool CanDispatchParticipantCommand(
            string partyId,
            string sessionId,
            ParticipantRoomCommand command,
            bool queued)
        {
            if (_participantDormancies.CanReceiveCommand(
                    partyId,
                    sessionId,
                    command))
            {
                return true;
            }

            _logger.Info(
                $"[Party {partyId}] {(queued ? "Dropping queued" : "Skipping")} " +
                $"{command} for dormant session {sessionId}; waiting for client " +
                "Start/Progress");
            return false;
        }

        private List<SessionInfo> GetRegisteredPartySessions(
            string partyId,
            ParticipantRoomCommand command)
        {
            var nowUtc = DateTime.UtcNow;
            var activeSessions = _sessionManager.Sessions
                .Where(session => session != null
                    && !string.IsNullOrEmpty(session.Id)
                    && PartySessionLivenessPolicy.IsOnline(session, nowUtc))
                .GroupBy(session => session.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            return _plugin.PartyParticipants.GetSessions(partyId)
                .Where(participant => participant != null
                    && _participantDormancies.CanReceiveCommand(
                        partyId,
                        participant.SessionId,
                        command))
                .Select(participant => participant.SessionId)
                .Where(sessionId => !string.IsNullOrEmpty(sessionId))
                .Distinct(StringComparer.Ordinal)
                .Where(activeSessions.ContainsKey)
                .Select(sessionId => activeSessions[sessionId])
                .ToList();
        }

        private SessionInfo GetMasterSessionForCommand(WatchPartyItem party)
        {
            var activeMaster = GetActiveMasterSession(party);
            if (activeMaster != null)
            {
                return activeMaster;
            }

            var masterSessionId = _plugin.PartyParticipants.GetMasterSession(party?.Id);
            return string.IsNullOrEmpty(masterSessionId)
                ? null
                : _sessionManager.Sessions.FirstOrDefault(session =>
                    string.Equals(session.Id, masterSessionId, StringComparison.Ordinal)
                    && PartySessionLivenessPolicy.IsOnline(session, DateTime.UtcNow));
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
            _masterSeekSources.ClearParty(partyId);
            foreach (var participant in sessions)
            {
                _participantResumeCoordinator.CancelSession(participant.SessionId);
                _playbackSyncCoordinator.ClearSession(participant.SessionId);
                _participantResumeLatencies.ClearSession(participant.SessionId);
                _seriesEpisodeTransitions.ClearSession(participant.SessionId);
            }

            CancelPendingMasterSeekSync(partyId);
            CancelPendingMasterPauseCommit(partyId);
            _partyPlaybackTransitions.Cancel(partyId);
            lock (_seriesTransitionLock)
            {
                _partiesTransitioning.Remove(partyId);
            }

            _partySyncedSessions.TryRemove(partyId, out _);
            _partySessionPauseState.TryRemove(partyId, out _);
            _lastProgressCheckpoint.TryRemove(partyId, out _);
            _participantDormancies.ClearParty(partyId);
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

        /// <summary>
        /// Handles the dedicated seek notification emitted by the patched Emby Web
        /// playback manager. The request is resolved to the authenticated Web session
        /// and then enters the same per-session queue as native playback events, so a
        /// late heartbeat cannot overtake the explicit user action.
        /// </summary>
        public Task<bool> HandleExplicitMasterSeekAsync(
            string userId,
            string deviceId,
            string itemId,
            string playSessionId,
            long positionTicks)
        {
            if (string.IsNullOrWhiteSpace(userId)
                || positionTicks < 0)
            {
                return Task.FromResult(false);
            }

            var session = ResolveExplicitMasterSession(userId, deviceId, itemId);
            if (session == null)
            {
                _logger.Debug(
                    $"[Watch Party] Ignoring explicit seek: no unique active session " +
                    $"matched user {userId}, device {deviceId ?? "<none>"}, item {itemId ?? "<none>"}");
                return Task.FromResult(false);
            }

            var result = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            // A drag can invoke PlaybackManager.seek several times before the
            // previous notification reaches the server. Keep only the newest queued
            // target within one playback generation; a delayed seek from a retired
            // generation must not cancel a current queued target. A seek already being
            // sent is allowed to finish, while its per-participant command queue
            // coalesces the next target as well.
            if (!_playbackEventQueue.TryEnqueueLatest(
                    session.Id,
                    coalescingKey: $"explicit-master-seek:{playSessionId ?? "<none>"}",
                    async cancellationToken =>
                    {
                        try
                        {
                            result.TrySetResult(
                                await HandleExplicitMasterSeekCoreAsync(
                                    session.Id,
                                    userId,
                                    deviceId,
                                    itemId,
                                    playSessionId,
                                    positionTicks,
                                    cancellationToken).ConfigureAwait(false));
                        }
                        catch (Exception ex)
                        {
                            result.TrySetException(ex);
                            throw;
                        }
                    },
                    _lifetimeCts.Token,
                    out var completion))
            {
                _logger.Warn(
                    $"[Watch Party] Dropping explicit seek for session {session.Id}: " +
                    "the bounded per-session event queue is full");
                result.TrySetResult(false);
                return result.Task;
            }

            // If shutdown cancels the queued item before its delegate starts, complete
            // the HTTP request rather than leaving it waiting forever.
            _ = completion.ContinueWith(
                task =>
                {
                    if (task.IsCanceled)
                    {
                        result.TrySetResult(false);
                    }
                    else if (task.IsFaulted && !result.Task.IsCompleted)
                    {
                        result.TrySetException(task.Exception.InnerException ?? task.Exception);
                    }
                },
                TaskScheduler.Default);
            return result.Task;
        }

        private SessionInfo ResolveExplicitMasterSession(
            string userId,
            string deviceId,
            string itemId)
        {
            var candidates = _sessionManager.Sessions
                .Where(session => session != null
                    && string.Equals(session.UserId, userId, StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrEmpty(itemId)
                        || string.Equals(
                            session.NowPlayingItem?.Id,
                            itemId,
                            StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (!string.IsNullOrEmpty(deviceId))
            {
                // The patched Web client always supplies its Emby device id. Do not
                // fall back to another session when that identity does not match;
                // doing so could let a second browser session on the same account
                // move the registered master's clock.
                candidates = candidates
                    .Where(session => string.Equals(
                        session.DeviceId,
                        deviceId,
                        StringComparison.Ordinal))
                    .ToList();
            }

            // When several browser sessions share an account/device, the registered
            // master session is the only unambiguous candidate. Otherwise require one
            // candidate instead of accidentally accepting a viewer's seek notification.
            foreach (var candidate in candidates)
            {
                var item = candidate.NowPlayingItem == null
                    ? null
                    : _libraryManager.GetItemById(candidate.NowPlayingItem.Id);
                var party = FindPartyForItem(item);
                if (party != null
                    && _plugin.PartyParticipants.IsMasterSession(party.Id, candidate.Id))
                {
                    return candidate;
                }
            }

            return candidates.Count == 1 ? candidates[0] : null;
        }

        private async Task<bool> HandleExplicitMasterSeekCoreAsync(
            string sessionId,
            string userId,
            string deviceId,
            string itemId,
            string playSessionId,
            long positionTicks,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = _sessionManager.Sessions.FirstOrDefault(candidate =>
                string.Equals(candidate?.Id, sessionId, StringComparison.Ordinal));
            if (session == null
                || !string.Equals(session.UserId, userId, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(deviceId)
                    && !string.Equals(session.DeviceId, deviceId, StringComparison.Ordinal))
                || (!string.IsNullOrEmpty(itemId)
                    && !string.Equals(
                        session.NowPlayingItem?.Id,
                        itemId,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            var item = session.NowPlayingItem == null
                ? null
                : _libraryManager.GetItemById(session.NowPlayingItem.Id);
            var party = FindPartyForItem(item);
            if (party == null || !party.IsActive || !CanUserJoinParty(party, session.UserId))
            {
                return false;
            }

            if (!IsMasterSession(party, session))
            {
                _logger.Debug(
                    $"[Party {party.Id}] Rejected explicit seek from non-master session " +
                    $"{session.Id}");
                return false;
            }

            if (!_plugin.PartyParticipants.IsCurrentPlaybackSession(
                    party.Id,
                    session.Id,
                    playSessionId))
            {
                _logger.Info(
                    $"[Party {party.Id}] Rejected explicit seek from stale playback " +
                    $"{playSessionId ?? "<none>"} in master session {session.Id}");
                return false;
            }

            positionTicks = Math.Max(0, positionTicks);
            var nowUtc = DateTime.UtcNow;
            var isPlaying = !party.IsWaitingRoom && party.IsPlaying;
            _playbackSyncCoordinator.ApplyExplicitMasterSeek(
                party.Id,
                positionTicks,
                isPlaying,
                nowUtc);
            _masterSeekSources.MarkExplicit(party.Id, session.Id);
            party.CurrentPositionTicks = positionTicks;
            CheckpointPartyProgress(party);
            CancelPendingMasterSeekSync(party.Id);

            _logger.Info(
                $"[Party {party.Id}] Accepted explicit Web master seek from session " +
                $"{session.Id} to {TimeSpan.FromTicks(positionTicks).TotalSeconds:F1}s");
            try
            {
                await SyncParticipantsToPosition(party, session.Id, positionTicks)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The master clock is authoritative even when one remote command
                // fails. Periodic calibration can retry that participant without
                // making the browser's explicit notification endpoint return 5XX.
                _logger.ErrorException(
                    $"[Party {party.Id}] Error syncing participants after explicit Web seek",
                    ex);
            }
            return true;
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
                && IsRegisteredPartyPlayback(party, activeSession));
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
                    var nowUtc = DateTime.UtcNow;
                    var masterWasInactiveBeforeStart = IsMasterSelectionSession(party, e.Session)
                        && !HasActiveMasterSession(party);
                    if (_plugin.PartyParticipants.IsRetiredPlaybackId(
                            party.Id,
                            e.Session.Id,
                            e.PlaySessionId))
                    {
                        _seriesSelections.Cancel(pendingSeriesSelection);
                        _logger.Debug(
                            $"[Party {party.Id}] Ignoring delayed PlaybackStart for retired " +
                            $"playback {e.PlaySessionId} on session {e.Session.Id}");
                        return;
                    }

                    if (!CanUserJoinParty(party, e.Session.UserId))
                    {
                        _logger.Warn($"[Party {party.Id}] Access denied for user {e.Session.UserId}");
                        await SendPlaystateCommandSerialAsync(
                            party.Id,
                            e.Session.Id,
                            e.Session.Id,
                            new PlaystateRequest { Command = PlaystateCommand.Stop });
                        return;
                    }

                    var userStartPosition = e.PlaybackPositionTicks ?? 0;
                    if (GetOrCreateParticipant(
                        party.Id,
                        e.Session,
                        e.PlaySessionId,
                        nowUtc,
                        party.MaxParticipants) == null)
                    {
                        await SendPlaystateCommandSerialAsync(
                            party.Id,
                            e.Session.Id,
                            e.Session.Id,
                            new PlaystateRequest { Command = PlaystateCommand.Stop });
                        return;
                    }
                    if (_participantDormancies.TryReactivate(
                            party.Id,
                            e.Session.Id,
                            nowUtc,
                            out var resumedFromStart))
                    {
                        resumedFromStart.Dispose();
                        _logger.Info(
                            $"[Party {party.Id}] Participant session {e.Session.Id} " +
                            "left dormancy after its accepted PlaybackStart");
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

                    if (party.IsSeriesParty
                        && isMaster
                        && masterWasInactiveBeforeStart
                        && masterSeriesSelection == null
                        && currentEpisode != null
                        && string.Equals(
                            startedEpisodeId,
                            currentEpisode.ItemId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        // A clean Stop followed by replaying the same episode is still
                        // a new authoritative playback generation. Followers were
                        // stopped with the old master, so Unpause/seek cannot revive
                        // them; mirror the new Start with PlayNow just like a cross-
                        // episode transition.
                        var restartSessions = GetSeriesTransitionSessions(party, e.Session)
                            .Where(session => !IsMasterSession(party, session))
                            .ToList();
                        if (restartSessions.Count > 0)
                        {
                            ResetSeriesEpisodeSyncState(party);
                            _logger.Info(
                                $"[Party {party.Id}] Master started a new playback generation " +
                                $"for {currentEpisode.ItemId}; restarting {restartSessions.Count} " +
                                "participant session(s)");
                            await PlaySeriesEpisodeForSessions(
                                party,
                                currentEpisode,
                                restartSessions,
                                userStartPosition);
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

                    UpdateParticipantActivity(
                        party.Id,
                        e.Session.Id,
                        userStartPosition,
                        e.IsPaused,
                        e.PlaySessionId);

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
                    }

                    if (party.IsWaitingRoom && !party.IsPlaying)
                    {
                        _logger.Info($"[Party {party.Id}] User {e.Session.UserId} started playback - marking as ready");

                        _plugin.PartyReadyUsers.SetReady(party.Id, e.Session.UserId, true);

                        _logger.Info($"[Party {party.Id}] Pausing user in waiting room");
                        await SendPauseStateCommand(party, e.Session, true);
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
                            party,
                            e.Session,
                            e.Item,
                            targetPosition,
                            controllingSession: GetMasterSessionForCommand(party));
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

                    // A paused master still owns an authoritative frozen clock. Keep
                    // running the pass so a participant whose native player rounded or
                    // ignored the first pause+seek can be corrected without requiring
                    // another user action.
                    if (party.IsWaitingRoom || !HasActiveMasterSession(party))
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

                    var sessions = GetRegisteredPartySessions(
                        party.Id,
                        ParticipantRoomCommand.Seek);
                    var maxBufferThreshold = TimeSpan.FromSeconds(party.MaxBufferThresholdSeconds).Ticks;

                    foreach (var session in sessions)
                    {
                        var item = session.NowPlayingItem == null
                            ? null
                            : _libraryManager.GetItemById(session.NowPlayingItem.Id);
                        var matchingParty = FindPartyForItem(item);

                        if (item == null || (matchingParty != null && matchingParty.Id == party.Id))
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

                            var isMaster = TryResolveMasterSession(party, session);
                            // SessionInfo.PlayState is shared by every PlaySessionId in
                            // one client session. A retired player can overwrite it after
                            // a newer generation has been accepted. The master therefore
                            // uses only the registry snapshot written by accepted progress;
                            // participant snapshots remain advisory for drift correction.
                            var reportedPosition = isMaster
                                ? participant.CurrentPositionTicks
                                : session.PlayState?.PositionTicks ?? 0;
                            var reportedIsPaused = isMaster
                                ? participant.IsPaused
                                : session.PlayState?.IsPaused ?? false;
                            var currentPosition = isMaster
                                ? participant.CurrentPositionTicks
                                : ParticipantPositionEstimator.Estimate(
                                    participant,
                                    reportedPosition,
                                    reportedIsPaused,
                                    nowUtc,
                                    MaxParticipantPositionProjection);
                            var positionDifference = Math.Abs(currentPosition - estimatedPartyPosition);

                            if (party.IsSeriesParty && !isMaster)
                            {
                                var episodeSwitchTarget = GetSeriesEpisodeCommandTarget(
                                    party,
                                    session.Id,
                                    item,
                                    nowUtc);
                                if (episodeSwitchTarget != null)
                                {
                                    // The transition tracker decides whether this is the
                                    // first command, a due confirmation retry, or a
                                    // duplicate periodic pass that must be suppressed.
                                    await PlaySeriesEpisodeForSessions(
                                        party,
                                        episodeSwitchTarget,
                                        new[] { session },
                                        estimatedPartyPosition);
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
                                var periodicTolerance = party.IsPlaying
                                    ? TimeSpan.FromSeconds(
                                        Math.Max(1, party.SyncToleranceSeconds))
                                    : TimeSpan.FromMilliseconds(250);
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
                                var controllingSession = GetMasterSessionForCommand(party);
                                await SyncUserToPosition(
                                    party,
                                    session,
                                    item,
                                    estimatedPartyPosition,
                                    controllingSession: controllingSession,
                                    applySyncOffset: party.IsPlaying);
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

        private async Task<bool> SyncUserToPosition(
            WatchPartyItem party,
            SessionInfo session,
            BaseItem item,
            long positionTicks,
            bool allowReplace = false,
            SessionInfo controllingSession = null,
            bool applySyncOffset = true,
            bool force = false,
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (session == null)
                {
                    return false;
                }

                if (!SupportsRemoteControlledPlayback(session))
                {
                    _logger.Info($"[Watch Party] Session {session.Id} ({session.Client}) does not advertise remote video control, skipping seek to {TimeSpan.FromTicks(positionTicks).TotalSeconds:F1}s");
                    return true;
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
                    return true;
                }

                var controllingSessionId = controllingSession?.Id ?? session.Id;
                _logger.Info(
                    $"[Watch Party] Syncing session {session.Id} to position {positionTicks} ticks " +
                    $"(adjusted: {adjustedPosition} with {appliedOffsetMilliseconds:+#;-#;0}ms offset, " +
                    $"controller: {controllingSessionId})");

                try
                {
                    var commandSent = await SendPlaystateCommandSerialAsync(
                        party?.Id,
                        controllingSessionId,
                        session.Id,
                        new PlaystateRequest
                        {
                            Command = PlaystateCommand.Seek,
                            SeekPositionTicks = adjustedPosition,
                            ControllingUserId = controllingSession?.UserId
                        },
                        cancellationToken);
                    if (!commandSent)
                    {
                        _playbackSyncCoordinator.CancelPendingSeek(
                            session.Id,
                            adjustedPosition);
                        return false;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _playbackSyncCoordinator.CancelPendingSeek(session.Id, adjustedPosition);
                    return false;
                }
                catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
                {
                    _playbackSyncCoordinator.CancelPendingSeek(session.Id, adjustedPosition);
                    return false;
                }
                catch (OperationCanceledException)
                {
                    // A newer master seek coalesced this queued command before it
                    // started. The replacement owns the coordinator state; do not
                    // report the superseded command as a failed sync.
                    _logger.Debug(
                        $"[Watch Party] Superseded queued seek for session {session.Id} " +
                        $"at {TimeSpan.FromTicks(adjustedPosition).TotalSeconds:F1}s");
                    return false;
                }
                catch
                {
                    _playbackSyncCoordinator.CancelPendingSeek(session.Id, adjustedPosition);
                    throw;
                }

                _logger.Info($"[Watch Party] Server accepted seek command for session {session.Id} (client confirmation pending)");
                return true;
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"[Watch Party] Error syncing session {session.Id}", ex);
                return false;
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
                _mediaIdentityResolver.FromPlaybackItem(item),
                _mediaIdentityResolver.ResolveConfiguredItem);
        }

        private static bool IsEmbyWebSession(SessionInfo session)
        {
            return PartySessionLivenessPolicy.IsWebClient(session?.Client);
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

        private WatchPartyEpisode GetSeriesEpisodeCommandTarget(
            WatchPartyItem party,
            string sessionId,
            BaseItem item,
            DateTime nowUtc)
        {
            var switchTarget = GetSeriesEpisodeSwitchTarget(party, item);
            if (switchTarget != null)
            {
                return switchTarget;
            }

            var currentEpisode = SeriesPartyQueue.GetCurrentEpisode(party);
            return item == null
                && currentEpisode != null
                && _seriesEpisodeTransitions.IsExpectedStart(
                    sessionId,
                    currentEpisode.ItemId,
                    nowUtc)
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
                    // Validate access before any playback-generation mutation. A
                    // QualityChange from a session that has just lost room access must
                    // not be able to retire the current generation and then get rejected
                    // only after the registry has already been changed.
                    if (!CanUserJoinParty(party, e.Session.UserId, logDenied: false))
                    {
                        _logger.Debug(
                            $"[Party {party.Id}] Ignoring progress from unauthorized or over-capacity user {e.Session.UserId}");
                        return;
                    }

                    // One Emby Web SessionId can briefly retain several PlaySessionIds
                    // after a seek or stream reload. Keep the strict identity check for
                    // ordinary progress, but allow Emby's explicit QualityChange event to
                    // establish the replacement when it omitted PlaybackStart. Without
                    // this narrow exception a healthy Web player can keep reporting under
                    // the new id while the room remains frozen on the old generation.
                    var acceptsPlaybackProgress = _plugin.PartyParticipants.IsCurrentPlaybackSession(
                        party.Id,
                        e.Session.Id,
                        e.PlaySessionId);
                    var adoptedPlaybackGeneration = false;
                    string previousPlaybackSessionId = null;
                    if (!acceptsPlaybackProgress
                        && PlaybackGenerationAdoptionPolicy.IsQualityChange(e.EventName))
                    {
                        adoptedPlaybackGeneration = _plugin.PartyParticipants
                            .TryAdoptQualityChangePlayback(
                                party.Id,
                                e.Session.Id,
                                e.PlaySessionId,
                                nowUtc,
                                out previousPlaybackSessionId);
                        acceptsPlaybackProgress = adoptedPlaybackGeneration;
                        if (adoptedPlaybackGeneration)
                        {
                            _playbackSyncCoordinator.ResetForNewPlayback(
                                e.Session.Id,
                                previousPlaybackSessionId,
                                e.PlaySessionId);

                            // A participant replacement must re-enter the initial-sync
                            // path. Preserve the master pause state so a stream quality
                            // change itself cannot look like a user pause/resume action.
                            if (!IsMasterSession(party, e.Session))
                            {
                                ClearSessionEpisodeMarkers(party.Id, e.Session.Id);
                            }

                            _logger.Info(
                                $"[Party {party.Id}] Adopted playback generation " +
                                $"{e.PlaySessionId} after QualityChange without " +
                                $"PlaybackStart; retired {previousPlaybackSessionId ?? "<none>"} " +
                                $"for session {e.Session.Id}");
                        }
                    }
                    if (!acceptsPlaybackProgress)
                    {
                        _logger.Debug(
                            $"[Party {party.Id}] Ignoring stale progress from playback " +
                            $"{e.PlaySessionId} (event={e.EventName}) for " +
                            $"session {e.Session.Id}");
                        return;
                    }

                    var hadKnownParticipant = _plugin.PartyParticipants.TryGetSession(
                        party.Id,
                        e.Session.Id,
                        out var previousParticipant);
                    var participantReturnedAfterGap = hadKnownParticipant
                        && ParticipantReconnectionPolicy.RequiresResync(
                            previousParticipant.LastActivityAt,
                            nowUtc,
                            ParticipantReconciliationGap);

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
                    var isMaster = TryResolveMasterSession(party, e.Session);
                    ParticipantDormancyTracker.Registration resumedRegistration = null;
                    var resumedFromDormancy = !isMaster
                        && _participantDormancies.TryReactivate(
                            party.Id,
                            e.Session.Id,
                            nowUtc,
                            out resumedRegistration);
                    if (resumedFromDormancy)
                    {
                        resumedRegistration.Dispose();
                        _logger.Info(
                            $"[Party {party.Id}] Participant session {e.Session.Id} returned " +
                            "after a transient Emby/iOS background Stop; restoring control state");
                    }
                    var wasInSyncedSet = _partySyncedSessions.TryGetValue(
                            party.Id,
                            out var knownSyncedSessions)
                        && knownSyncedSessions.ContainsKey(e.Session.Id);
                    var hasPriorPauseState = pauseState.TryGetValue(
                        e.Session.Id,
                        out var previousPause);
                    var wasPaused = hasPriorPauseState && previousPause;
                    // A client can reappear with a new PlaySessionId without sending
                    // PlaybackStart (for example after a slow seek/rebuffer). Its first
                    // progress packet is a state snapshot, not a fresh viewer control
                    // action. In particular, iOS commonly sends the old paused state
                    // while the server is already re-syncing it to the master clock.
                    var isInitialParticipantReport = !isMaster
                        && (adoptedPlaybackGeneration
                            || (!wasInSyncedSet && !hasPriorPauseState));
                    var reportedPosition = e.PlaybackPositionTicks;
                    var ignoreParticipantPosition = false;
                    var pendingParticipantTarget = 0L;
                    if (!isMaster && reportedPosition.HasValue)
                    {
                        ignoreParticipantPosition = _playbackSyncCoordinator.ShouldIgnorePositionReport(
                            e.Session.Id,
                            reportedPosition.Value,
                            nowUtc,
                            out pendingParticipantTarget);
                    }

                    var currentPosition = ignoreParticipantPosition
                        ? pendingParticipantTarget
                        : reportedPosition ?? 0;
                    var effectiveReportedPosition = ignoreParticipantPosition
                        ? (long?)pendingParticipantTarget
                        : reportedPosition;
                    if (ignoreParticipantPosition)
                    {
                        _logger.Debug(
                            $"[Party {party.Id}] Ignoring stale position " +
                            $"{TimeSpan.FromTicks(reportedPosition.Value).TotalSeconds:F1}s from participant " +
                            $"{e.Session.Id}; pending master target is " +
                            $"{TimeSpan.FromTicks(pendingParticipantTarget).TotalSeconds:F1}s");
                    }
                    var inboundPauseState = _playbackSyncCoordinator.ClassifyInboundPauseState(
                        e.Session.Id,
                        wasPaused,
                        e.IsPaused,
                        reportedPosition,
                        nowUtc);
                    if (!isMaster
                        && !e.IsPaused
                        && inboundPauseState.IsExpectedCommandEcho
                        && _participantResumeLatencies.TryRecordResumeAcknowledgement(
                            e.Session.Id,
                            inboundPauseState.MatchedExpectations,
                            nowUtc,
                            out var observedResumeLatency,
                            out var estimatedResumeLatency))
                    {
                        _logger.Info(
                            $"[Party {party.Id}] Learned resume latency for session " +
                            $"{e.Session.Id}: observed " +
                            $"{observedResumeLatency.TotalMilliseconds:F0}ms, estimate " +
                            $"{estimatedResumeLatency.TotalMilliseconds:F0}ms");
                    }

                    // Emby Web can report a position-less state and then immediately
                    // report the opposite state while a seek/rebuffer rebuilds the video
                    // element. Do not let that transient reversal become a room-wide
                    // command. A stable transition is committed by a short delayed task.
                    var deferMasterPauseTransition = false;
                    var ignoreMasterPauseTransition = false;
                    if (!adoptedPlaybackGeneration
                        && isMaster
                        && !WaitingRoomPolicy.SuppressesPlaybackControl(party)
                        && inboundPauseState.IsTransition
                        && !inboundPauseState.IsExpectedCommandEcho)
                    {
                        var masterObservation = _masterPlaybackStateDebouncer.Observe(
                            e.Session.Id,
                            wasPaused,
                            e.IsPaused,
                            nowUtc);
                        deferMasterPauseTransition = masterObservation.ShouldDefer;
                        ignoreMasterPauseTransition = masterObservation.ShouldCancel;
                        if (deferMasterPauseTransition)
                        {
                            ScheduleMasterPauseStateCommit(
                                party,
                                e.Session,
                                e.IsPaused,
                                reportedPosition,
                                e.PlaySessionId);
                        }
                        else if (ignoreMasterPauseTransition)
                        {
                            _logger.Debug(
                                $"[Party {party.Id}] Ignoring transient master state reversal " +
                                $"for session {e.Session.Id}");
                        }
                    }

                    var applyPauseTransition = PlaybackGenerationAdoptionPolicy.ShouldApplyPauseTransition(
                        adoptedPlaybackGeneration,
                        isMaster,
                        deferMasterPauseTransition,
                        ignoreMasterPauseTransition);
                    var effectiveIsPaused = PlaybackGenerationAdoptionPolicy.ResolveEffectivePauseState(
                        adoptedPlaybackGeneration,
                        wasPaused,
                        e.IsPaused,
                        applyPauseTransition);
                    var pauseTransitionAction = PauseTransitionPolicy.Decide(
                        isMaster
                            ? PlaybackStateReporterRole.Master
                            : PlaybackStateReporterRole.Participant,
                        wasPaused,
                        e.IsPaused,
                        party.IsPlaying,
                        HasActiveMasterSession(party));

                    if (!ignoreParticipantPosition)
                    {
                        UpdateParticipantActivity(
                            party.Id,
                            e.Session.Id,
                            currentPosition,
                            effectiveIsPaused,
                            e.PlaySessionId);
                    }

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
                        if ((adoptedPlaybackGeneration
                                || resumedFromDormancy
                                || participantReturnedAfterGap
                                || !syncedSessions.ContainsKey(e.Session.Id))
                            && HasActiveMasterSession(party)
                            && (resumedFromDormancy
                                || !_playbackSyncCoordinator.IsSeekSettling(e.Session.Id, nowUtc)))
                        {
                            var targetPosition = _playbackSyncCoordinator.GetEstimatedPartyPosition(
                                party.Id,
                                party.CurrentPositionTicks,
                                nowUtc);

                            _logger.Info(
                                $"[Party {party.Id}] Session {e.Session.Id} resumed party playback without a PlaybackStart event " +
                                $"(user at {TimeSpan.FromTicks(currentPosition).TotalSeconds:F1}s, party at " +
                                $"{TimeSpan.FromTicks(targetPosition).TotalSeconds:F1}s); reconciling playback state");
                            var reconciliationSucceeded = await ReconcileParticipantAfterReconnect(
                                party,
                                e.Session,
                                e.Item,
                                currentPosition,
                                targetPosition);

                            if (reconciliationSucceeded)
                            {
                                syncedSessions.TryAdd(e.Session.Id, 0);
                            }
                            else
                            {
                                _logger.Warn(
                                    $"[Party {party.Id}] Reconnection reconciliation for " +
                                    $"session {e.Session.Id} was not accepted; will retry on the next report");
                            }
                        }
                    }

                    if (!ignoreParticipantPosition
                        && _playbackSyncCoordinator.ConfirmSeekTarget(e.Session.Id, currentPosition, nowUtc))
                    {
                        _confirmedPlaybackSeekRetrier.Confirm(e.Session.Id);
                        _logger.Debug($"[Party {party.Id}] Session {e.Session.Id} confirmed seek target at {TimeSpan.FromTicks(currentPosition).TotalSeconds:F1}s");
                    }

                    if (WaitingRoomPolicy.SuppressesPlaybackControl(party))
                    {
                        // Waiting-room progress is readiness/activity, never a room-control
                        // signal. A local resume (or an uncontrollable client that never
                        // honored the initial Pause) must not become a room-wide action.
                        // Reassert once per reported transition;
                        // repeated unpaused progress is not allowed to create a command storm.
                        if (WaitingRoomPolicy.ShouldRestorePause(party, effectiveIsPaused)
                            && (!hasPriorPauseState || wasPaused))
                        {
                            _logger.Info(
                                $"[Party {party.Id}] Ignoring resume from session {e.Session.Id} " +
                                "while the waiting room owns playback state");
                            await SendPauseStateCommand(party, e.Session, true);
                        }
                    }
                    else if (inboundPauseState.IsTransition
                        && inboundPauseState.IsSyntheticEcho)
                    {
                        _logger.Debug(
                            $"[Party {party.Id}] Ignoring playback-state echo from session {e.Session.Id} " +
                            "during seek settlement");
                    }
                    else if (isInitialParticipantReport)
                    {
                        _logger.Debug(
                            $"[Party {party.Id}] Ignoring initial participant playback state " +
                            $"from session {e.Session.Id} as a control transition");
                    }
                    else if (applyPauseTransition)
                    {
                        await PlaybackStateAuthorityDispatcher.Dispatch(
                            pauseTransitionAction,
                            e.IsPaused,
                            () => RestoreParticipantPlaybackState(party, e.Session),
                            () => HandleMasterPause(
                                party,
                                e.Session,
                                effectiveReportedPosition),
                            () => HandleMasterResume(party, e.Session));
                    }

                    pauseState[e.Session.Id] = effectiveIsPaused;

                    if (isMaster)
                    {
                        var partyWasPlayingBeforeMasterReport = party.IsPlaying;
                        if (!party.IsWaitingRoom)
                        {
                            party.IsPlaying = !effectiveIsPaused;
                        }
                        if (reportedPosition.HasValue)
                        {
                            var masterPositionUpdate =
                                _playbackSyncCoordinator.UpdateMasterPositionGuardingReloadArtifact(
                                    party.Id,
                                    currentPosition,
                                    !effectiveIsPaused && !party.IsWaitingRoom,
                                    nowUtc,
                                    TimeSpan.FromSeconds(party.SyncToleranceSeconds).Ticks,
                                    MasterSeekNearZeroThreshold.Ticks,
                                    MasterSeekDrasticFromThreshold.Ticks,
                                    // A tab loaded before the dashboard patch has no
                                    // dedicated seek request. Keep the hardened legacy
                                    // near-zero/sequence debounce as a compatibility
                                    // fallback until this exact Web session has sent one
                                    // explicit seek. Once explicit seeks are observed,
                                    // ordinary heartbeats cannot move the master clock.
                                    acceptInferredSeek: !IsEmbyWebSession(e.Session)
                                        || !_masterSeekSources.HasExplicitForSession(
                                            party.Id,
                                            e.Session.Id));

                            if (masterPositionUpdate.IsIgnoredReloadArtifactReport)
                            {
                                party.CurrentPositionTicks = masterPositionUpdate.AuthoritativePositionTicks;
                                _logger.Debug(
                                    $"[Party {party.Id}] Ignoring stale master position " +
                                    $"{TimeSpan.FromTicks(currentPosition).TotalSeconds:F1}s immediately after reload; " +
                                    $"authoritative clock remains {TimeSpan.FromTicks(party.CurrentPositionTicks).TotalSeconds:F1}s");
                                return;
                            }

                            if (masterPositionUpdate.IsIgnoredUnexpectedDiscontinuity)
                            {
                                party.CurrentPositionTicks = masterPositionUpdate.AuthoritativePositionTicks;
                                _logger.Debug(
                                    $"[Party {party.Id}] Ignoring discontinuous regular Web progress " +
                                    $"{TimeSpan.FromTicks(currentPosition).TotalSeconds:F1}s; " +
                                    $"explicit seek notification is required to move the master clock");
                                return;
                            }

                            if (masterPositionUpdate.IsIgnoredStaleHeartbeat)
                            {
                                // A third-party master can keep reporting the last
                                // position after its player instance has stopped
                                // advancing. Preserve the projected authoritative clock
                                // but do not turn every heartbeat into another seek or
                                // undo a participant's accepted pause with a stale
                                // "still playing" heartbeat. A real master resume is
                                // retained because it arrives as a pause-state transition.
                                if (!effectiveIsPaused
                                    && !partyWasPlayingBeforeMasterReport
                                    && !inboundPauseState.IsTransition
                                    && !party.IsWaitingRoom)
                                {
                                    party.IsPlaying = false;
                                }
                                party.CurrentPositionTicks = masterPositionUpdate.AuthoritativePositionTicks;
                                _logger.Debug(
                                    $"[Party {party.Id}] Ignoring stationary master heartbeat " +
                                    $"at {TimeSpan.FromTicks(currentPosition).TotalSeconds:F1}s; " +
                                    $"authoritative clock remains {TimeSpan.FromTicks(party.CurrentPositionTicks).TotalSeconds:F1}s");
                                return;
                            }

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
                            if (masterPositionUpdate.IsSeek
                                && !(applyPauseTransition && e.IsPaused && !wasPaused))
                            {
                                _logger.Info(
                                    $"[Party {party.Id}] Master seeked to " +
                                    $"{TimeSpan.FromTicks(currentPosition).TotalSeconds:F1}s; scheduling participant sync");
                                ScheduleMasterSeekSync(
                                    party,
                                    e.Session.Id,
                                    currentPosition,
                                    immediate: false);
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
                                !effectiveIsPaused && !party.IsWaitingRoom,
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

        private void ScheduleMasterPauseStateCommit(
            WatchPartyItem party,
            SessionInfo masterSession,
            bool isPaused,
            long? reportedPositionTicks,
            string playSessionId)
        {
            if (party == null || masterSession == null)
            {
                return;
            }

            lock (_masterPauseCommitLock)
            {
                if (_pendingMasterPauseCommits.TryGetValue(party.Id, out var existing))
                {
                    if (string.Equals(existing.SessionId, masterSession.Id, StringComparison.Ordinal)
                        && existing.IsPaused == isPaused)
                    {
                        // The same transition may be reported more than once while the
                        // video element is settling. Keep one timer and one command.
                        existing.ReportedPositionTicks = reportedPositionTicks
                            ?? existing.ReportedPositionTicks;
                        existing.PlaySessionId = playSessionId ?? existing.PlaySessionId;
                        return;
                    }

                    existing.WindowCts.Cancel();
                    existing.WindowCts.Dispose();
                    _pendingMasterPauseCommits.Remove(party.Id);
                }

                var pending = new PendingMasterPauseCommit
                {
                    PartyId = party.Id,
                    SessionId = masterSession.Id,
                    IsPaused = isPaused,
                    ReportedPositionTicks = reportedPositionTicks,
                    PlaySessionId = playSessionId,
                    WindowCts = new CancellationTokenSource()
                };
                _pendingMasterPauseCommits[party.Id] = pending;
                _ = CompleteMasterPauseStateCommitAsync(party, pending, pending.WindowCts.Token);
            }
        }

        private async Task CompleteMasterPauseStateCommitAsync(
            WatchPartyItem party,
            PendingMasterPauseCommit pending,
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(_masterPlaybackStateDebouncer.Window, cancellationToken)
                    .ConfigureAwait(false);

                if (!_plugin.PartyParticipants.IsCurrentPlaybackSession(
                        party.Id,
                        pending.SessionId,
                        pending.PlaySessionId))
                {
                    _masterPlaybackStateDebouncer.Cancel(pending.SessionId);
                    _logger.Debug(
                        $"[Party {party.Id}] Discarding delayed master state from " +
                        $"stale playback {pending.PlaySessionId ?? "<none>"}");
                    return;
                }

                var session = _sessionManager.Sessions.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, pending.SessionId, StringComparison.Ordinal));
                if (session == null || !IsMasterSession(party, session))
                {
                    _masterPlaybackStateDebouncer.Cancel(pending.SessionId);
                    return;
                }

                var pauseState = _partySessionPauseState.GetOrAdd(
                    party.Id,
                    _ => new ConcurrentDictionary<string, bool>());
                var stableIsPaused = pauseState.TryGetValue(
                    pending.SessionId,
                    out var currentIsPaused)
                    && currentIsPaused;
                if (!_masterPlaybackStateDebouncer.TryCommit(
                        pending.SessionId,
                        stableIsPaused,
                        DateTime.UtcNow,
                        out var committedIsPaused))
                {
                    return;
                }

                var nowUtc = DateTime.UtcNow;
                if (pending.ReportedPositionTicks.HasValue)
                {
                    var position = Math.Max(0, pending.ReportedPositionTicks.Value);
                    party.CurrentPositionTicks = position;
                    _playbackSyncCoordinator.UpdateMasterPosition(
                        party.Id,
                        position,
                        !committedIsPaused && !party.IsWaitingRoom,
                        nowUtc,
                        TimeSpan.FromSeconds(party.SyncToleranceSeconds).Ticks);
                }
                else
                {
                    party.CurrentPositionTicks = _playbackSyncCoordinator.SetMasterPlaybackState(
                        party.Id,
                        !committedIsPaused && !party.IsWaitingRoom,
                        party.CurrentPositionTicks,
                        nowUtc);
                }

                party.IsPlaying = !committedIsPaused;
                pauseState[pending.SessionId] = committedIsPaused;

                _logger.Info(
                    $"[Party {party.Id}] Committed stable master " +
                    $"{(committedIsPaused ? "pause" : "resume")} after Web state debounce");
                if (committedIsPaused)
                {
                    await HandleMasterPause(
                        party,
                        session,
                        reportedPositionTicks: pending.ReportedPositionTicks);
                }
                else
                {
                    await HandleMasterResume(party, session);
                }
            }
            catch (OperationCanceledException)
            {
                // Superseded by an opposite state or plugin shutdown.
            }
            catch (Exception ex)
            {
                _logger.ErrorException(
                    $"[Party {party.Id}] Error committing debounced master state",
                    ex);
            }
            finally
            {
                lock (_masterPauseCommitLock)
                {
                    if (_pendingMasterPauseCommits.TryGetValue(party.Id, out var current)
                        && ReferenceEquals(current, pending))
                    {
                        _pendingMasterPauseCommits.Remove(party.Id);
                    }
                }
            }
        }

        private void ScheduleMasterSeekSync(
            WatchPartyItem party,
            string masterSessionId,
            long positionTicks,
            bool isReloadArtifact = false,
            long authoritativeRevision = 0,
            long stagedAuthoritativePositionTicks = 0,
            bool immediate = false)
        {
            var debounce = immediate
                ? TimeSpan.Zero
                : isReloadArtifact
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
            var sessions = GetRegisteredPartySessions(
                party.Id,
                ParticipantRoomCommand.Seek);
            foreach (var session in sessions)
            {
                if (session.Id == masterSessionId || IsMasterSession(party, session))
                {
                    continue;
                }

                var item = session.NowPlayingItem == null
                    ? null
                    : _libraryManager.GetItemById(session.NowPlayingItem.Id);
                var matchingParty = FindPartyForItem(item);
                if ((item == null || (matchingParty != null && matchingParty.Id == party.Id))
                    && _plugin.PartyParticipants.TryGetSession(
                        party.Id,
                        session.Id,
                        out _))
                {
                    var episodeSwitchTarget = GetSeriesEpisodeCommandTarget(
                        party,
                        session.Id,
                        item,
                        DateTime.UtcNow);
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
                        string.Equals(candidate.Id, masterSessionId, StringComparison.Ordinal))
                        ?? GetMasterSessionForCommand(party);
                    await SyncUserToPosition(
                        party,
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
                if (!string.IsNullOrEmpty(participant.SessionId)
                    && _participantDormancies.CanReceiveCommand(
                        party.Id,
                        participant.SessionId,
                        ParticipantRoomCommand.PlayNow))
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
                    PlaybackControlCapabilities.SessionSupportsRemoteControl(session),
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

            foreach (var session in sessions)
            {
                if (session != null
                    && !CanDispatchParticipantCommand(
                        party.Id,
                        session.Id,
                        ParticipantRoomCommand.PlayNow,
                        queued: false))
                {
                    _seriesEpisodeTransitions.CancelExpectedStart(
                        session.Id,
                        episode.ItemId);
                    continue;
                }

                if (session == null || !SupportsRemoteControlledPlayback(session))
                {
                    if (session != null)
                    {
                        _seriesEpisodeTransitions.CancelExpectedStart(
                            session.Id,
                            episode.ItemId);
                    }
                    _logger.Info($"[Party {party.Id}] Skipping {session?.UserName} ({session?.Client}): session does not support remote-controlled playback");
                    continue;
                }

                try
                {
                    var nowUtc = DateTime.UtcNow;
                    _seriesEpisodeTransitions.RemoveExpired(nowUtc);
                    if (!_seriesEpisodeTransitions.TryBeginCommandAttempt(
                        session.Id,
                        episode.ItemId,
                        nowUtc,
                        SeriesCommandConfirmationRetryInterval,
                        SeriesCommandMaxAttempts,
                        out var attempt))
                    {
                        continue;
                    }

                    var playRequest = new PlayRequest
                    {
                        ItemIds = new[] { episodeItem.InternalId },
                        MediaSourceId = episode.MediaSourceId,
                        PlayCommand = PlayCommand.PlayNow,
                        StartPositionTicks = Math.Max(0, startPositionTicks)
                    };
                    if (attempt == 1)
                    {
                        _ = RetrySeriesEpisodeUntilConfirmedAsync(
                            party.Id,
                            episode.ItemId,
                            session.Id);
                    }
                    var commandSent = await SendPlayCommandSerialAsync(
                        party.Id,
                        session.Id,
                        session.Id,
                        playRequest,
                        commandCancellationToken);
                    if (!commandSent)
                    {
                        _seriesEpisodeTransitions.CancelExpectedStart(
                            session.Id,
                            episode.ItemId);
                        continue;
                    }
                    _logger.Info(
                        $"[Party {party.Id}] Episode command sent to {session.UserName} " +
                        $"for {episode.ItemName} (attempt {attempt}/{SeriesCommandMaxAttempts}); " +
                        "waiting for PlaybackStart confirmation");
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
                    _logger.ErrorException(
                        $"[Party {party.Id}] Episode command for {episode.ItemName} was not " +
                        $"accepted by session {session.Id}; a bounded retry remains pending",
                        ex);
                }
            }
        }

        private async Task RetrySeriesEpisodeUntilConfirmedAsync(
            string partyId,
            string episodeItemId,
            string sessionId)
        {
            try
            {
                while (!_lifetimeCts.IsCancellationRequested)
                {
                    await Task.Delay(
                            SeriesCommandConfirmationRetryInterval,
                            _lifetimeCts.Token)
                        .ConfigureAwait(false);

                    var nowUtc = DateTime.UtcNow;
                    if (!_seriesEpisodeTransitions.IsExpectedStart(
                            sessionId,
                            episodeItemId,
                            nowUtc))
                    {
                        return;
                    }

                    WatchPartyItem party;
                    lock (_plugin.ConfigurationSyncRoot)
                    {
                        party = _plugin.Configuration.WatchParties.FirstOrDefault(candidate =>
                            candidate.IsActive
                            && string.Equals(candidate.Id, partyId, StringComparison.Ordinal));
                    }

                    var currentEpisode = SeriesPartyQueue.GetCurrentEpisode(party);
                    if (currentEpisode == null
                        || !string.Equals(
                            currentEpisode.ItemId,
                            episodeItemId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    if (!_plugin.PartyParticipants.TryGetSession(
                            partyId,
                            sessionId,
                            out _))
                    {
                        return;
                    }

                    if (!CanDispatchParticipantCommand(
                            partyId,
                            sessionId,
                            ParticipantRoomCommand.PlayNow,
                            queued: false))
                    {
                        _seriesEpisodeTransitions.CancelExpectedStart(
                            sessionId,
                            episodeItemId);
                        return;
                    }

                    var session = _sessionManager.Sessions.FirstOrDefault(candidate =>
                        string.Equals(candidate.Id, sessionId, StringComparison.Ordinal));
                    if (session == null || !SupportsRemoteControlledPlayback(session))
                    {
                        // The iOS session may temporarily disappear while rebuilding
                        // its player. Keep the bounded confirmation window alive and
                        // retry if it returns before expiration.
                        continue;
                    }

                    var targetPosition = _playbackSyncCoordinator.GetEstimatedPartyPosition(
                        party.Id,
                        party.CurrentPositionTicks,
                        nowUtc);
                    await PlaySeriesEpisodeForSessions(
                        party,
                        currentEpisode,
                        new[] { session },
                        targetPosition,
                        _lifetimeCts.Token);
                }
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
            {
                // Plugin shutdown cancels pending confirmation retries.
            }
            catch (Exception ex)
            {
                _logger.ErrorException(
                    $"[Party {partyId}] Episode confirmation retry loop failed for session {sessionId}",
                    ex);
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

            _playbackSyncCoordinator.ClearParty(party.Id);
            foreach (var participant in _plugin.PartyParticipants.GetSessions(party.Id))
            {
                _playbackSyncCoordinator.ClearSession(participant.SessionId);
                _seriesEpisodeTransitions.ClearSession(participant.SessionId);
            }
            _plugin.PartyParticipants.ResetEpisodeState(party.Id);
        }

        private void OnPlaybackStopped(object sender, PlaybackStopEventArgs e)
        {
            QueuePlaybackEvent(
                e?.Session?.Id,
                "PlaybackStopped",
                () => HandlePlaybackStoppedAsync(e));
        }

        private void MarkParticipantDormant(
            WatchPartyItem party,
            string sessionId,
            string playSessionId,
            string userName,
            DateTime stoppedAtUtc)
        {
            _participantDormancies.MarkDormant(
                party.Id,
                sessionId,
                playSessionId,
                stoppedAtUtc);
            _participantResumeCoordinator.CancelSession(sessionId);
            _participantResumeLatencies.CancelPendingResume(sessionId);
            _logger.Info(
                $"[Party {party.Id}] Retaining participant {userName} " +
                $"({sessionId}) after transient Stop while the master remains active");
        }

        private async Task HandlePlaybackStoppedAsync(PlaybackStopEventArgs e)
        {
            try
            {
                _logger.Info($"[Watch Party] PlaybackStopped event fired - Item: {e.Item?.Name}, ItemId: {e.Item?.Id}, Session: {e.Session.Id}");

                var party = FindPartyForItem(e.Item);

                if (party != null && party.IsActive)
                {
                    if (!_plugin.PartyParticipants.TryGetSession(
                            party.Id,
                            e.Session.Id,
                            out var currentParticipant))
                    {
                        _logger.Info(
                            $"[Party {party.Id}] Ignoring Stop for unregistered playback " +
                            $"{e.PlaySessionId} on session {e.Session.Id}");
                        return;
                    }

                    if (!_plugin.PartyParticipants.IsCurrentPlaybackSession(
                            party.Id,
                            e.Session.Id,
                            e.PlaySessionId))
                    {
                        _logger.Info(
                            $"[Party {party.Id}] Ignoring delayed Stop for playback " +
                            $"{e.PlaySessionId}; session {e.Session.Id} now represents " +
                            $"{currentParticipant.PlaySessionId}");
                        return;
                    }

                    // Never promote or recreate a master from Stop. The participant
                    // snapshot was updated only by the accepted PlaySessionId, whereas
                    // Emby's shared SessionInfo.PlayState can have been overwritten by
                    // a zombie reporter from another episode.
                    var isMaster = IsMasterSession(party, e.Session);
                    var stoppedPosition = Math.Max(0, currentParticipant.CurrentPositionTicks);
                    var stoppedEpisodeId = FindSeriesEpisodeId(party, e.Item);
                    var currentEpisode = SeriesPartyQueue.GetCurrentEpisode(party);

                    if (!isMaster)
                    {
                        if (party.IsSeriesParty
                            && _seriesEpisodeTransitions.ShouldRetainSessionOnStop(
                                e.Session.Id,
                                stoppedEpisodeId,
                                currentEpisode?.ItemId,
                                DateTime.UtcNow))
                        {
                            _logger.Info(
                                $"[Party {party.Id}] Keeping participant session " +
                                $"{e.Session.Id} commandable while it switches from " +
                                $"episode {stoppedEpisodeId} to {currentEpisode?.ItemId}");
                            return;
                        }

                        MarkParticipantDormant(
                            party,
                            e.Session.Id,
                            e.PlaySessionId,
                            e.Session.UserName ?? e.Session.UserId,
                            DateTime.UtcNow);
                        return;
                    }

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

                        await StopParticipantsAfterMasterStop(
                            party,
                            e.Session.Id);
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
            if (ReferenceEquals(Current, this))
            {
                Current = null;
            }

            _sessionManager.PlaybackStart -= OnPlaybackStart;
            _sessionManager.PlaybackProgress -= OnPlaybackProgress;
            _sessionManager.PlaybackStopped -= OnPlaybackStopped;
            _plugin.ConfigurationUpdated -= OnConfigurationUpdated;

            _waitingRoomStartRegistration?.Dispose();
            _waitingRoomStartRegistration = null;

            _lifetimeCts.Cancel();
            _participantDormancies.Dispose();
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
            lock (_masterPauseCommitLock)
            {
                foreach (var pending in _pendingMasterPauseCommits.Values)
                {
                    pending.WindowCts.Cancel();
                    pending.WindowCts.Dispose();
                }
                _pendingMasterPauseCommits.Clear();
            }
            _masterPlaybackStateDebouncer.Clear();
            _participantResumeCoordinator.Clear();
            _participantResumeLatencies.Clear();
            _partyPlaybackTransitions.Dispose();

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

        private void CancelPendingMasterPauseCommit(string partyId)
        {
            if (string.IsNullOrEmpty(partyId))
            {
                return;
            }

            string masterSessionId;
            lock (_masterPauseCommitLock)
            {
                if (_pendingMasterPauseCommits.TryGetValue(partyId, out var pending))
                {
                    pending.WindowCts.Cancel();
                    pending.WindowCts.Dispose();
                    _pendingMasterPauseCommits.Remove(partyId);
                }
                masterSessionId = _plugin.PartyParticipants.GetMasterSession(partyId);
            }

            _masterPlaybackStateDebouncer.Cancel(masterSessionId);
        }

        private sealed class PendingMasterPauseCommit
        {
            public string PartyId { get; set; }
            public string SessionId { get; set; }
            public string PlaySessionId { get; set; }
            public bool IsPaused { get; set; }
            public long? ReportedPositionTicks { get; set; }
            public CancellationTokenSource WindowCts { get; set; }
        }
    }
}
