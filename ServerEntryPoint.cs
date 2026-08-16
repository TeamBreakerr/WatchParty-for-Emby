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
        private readonly Dictionary<string, string> _partyHostSessions = new Dictionary<string, string>();
        private readonly HashSet<string> _trackedPartyIds = new HashSet<string>();
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> _partyPauseVotes = new ConcurrentDictionary<string, ConcurrentDictionary<string, int>>();
        private readonly HashSet<string> _partiesTransitioning = new HashSet<string>();
        private readonly ConcurrentDictionary<string, long> _seriesSelectionVersions = new ConcurrentDictionary<string, long>();
        private readonly SeriesEpisodeTransitionTracker _seriesEpisodeTransitions =
            new SeriesEpisodeTransitionTracker();
        private readonly object _seriesTransitionLock = new object();
        private readonly ConcurrentDictionary<string, DateTime> _lastProgressCheckpoint = new ConcurrentDictionary<string, DateTime>();
        private readonly PlaybackSyncCoordinator _playbackSyncCoordinator = new PlaybackSyncCoordinator();
        private static readonly TimeSpan ProgressCheckpointInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan PartyValidationInterval = TimeSpan.FromMinutes(5);
        // Periodic calibration is a rare safety net, not a steady correction loop. Clients
        // report progress every 5-10s, so the cached position is inherently that stale
        // compared with the extrapolated master clock; a tight tolerance made the loop
        // re-command a seek every cycle and the participant's progress bar jumped
        // constantly. Only correct when the participant is clearly behind.
        private static readonly TimeSpan PeriodicSyncTolerance = TimeSpan.FromSeconds(15);
        // When the master drags the timeline, Emby Web emits a position-less "unknown"
        // report, then a real ~1s position while the video element is recreated, then the
        // real target up to ~9s later. Coalesce the burst into a single seek per drag so
        // the participant is not commanded to the transient near-zero position.
        private static readonly TimeSpan MasterSeekDebounce = TimeSpan.FromMilliseconds(1500);
        private static readonly TimeSpan MasterSeekNearZeroThreshold = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan MasterSeekDrasticFromThreshold = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan MasterSeekDrasticDebounce = TimeSpan.FromSeconds(10);
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
            
            _sessionManager.PlaybackStart += OnPlaybackStart;
            _sessionManager.PlaybackProgress += OnPlaybackProgress;
            _sessionManager.PlaybackStopped += OnPlaybackStopped;
            
            _plugin.ConfigurationUpdated += OnConfigurationUpdated;

            var requiresDirectItemUpgrade = _plugin.Configuration.ConfigurationVersion
                < PluginConfigurationMigration.DirectItemBindingVersion;
            var removedLegacyRoomCount = PluginConfigurationMigration.ResetLegacyRooms(_plugin.Configuration);
            if (requiresDirectItemUpgrade)
            {
                _plugin.SaveConfiguration();
                _logger.Info(
                    $"Direct-item configuration upgrade removed {removedLegacyRoomCount} legacy room(s)");
            }

            RepairSeriesPartyConfiguration();
            
            var config = _plugin.Configuration;
            foreach (var party in config.WatchParties)
            {
                _trackedPartyIds.Add(party.Id);
            }
            
            var intervalMs = Math.Max(1, config.SyncIntervalSeconds) * 1000;
            _syncTimer = new Timer(CheckAndSyncUsers, null, intervalMs, intervalMs);
            Task.Run(() => ValidateAndCleanWatchParties(force: true));
        }

        private void RepairSeriesPartyConfiguration()
        {
            var changed = false;
            foreach (var party in _plugin.Configuration.WatchParties)
            {
                if (party.AllowedUserIds == null)
                {
                    party.AllowedUserIds = new List<string>();
                    changed = true;
                }
                if (party.EpisodeQueue == null)
                {
                    party.EpisodeQueue = new List<WatchPartyEpisode>();
                    changed = true;
                }
                changed |= SeriesPartyQueue.Repair(party);
            }

            if (changed)
            {
                _plugin.SaveConfiguration();
                _logger.Info("Repaired persisted Series Party queue state");
            }
        }

        private void OnConfigurationUpdated(object sender, EventArgs e)
        {
            _logger.Info("Configuration updated, refreshing collections and timer");
            
            RepairSeriesPartyConfiguration();
            var config = _plugin.Configuration;
            var intervalMs = Math.Max(1, config.SyncIntervalSeconds) * 1000;
            _syncTimer?.Change(intervalMs, intervalMs);
            
            Task.Run(() => CleanupRemovedParties());
            Task.Run(() => ValidateAndCleanWatchParties(force: true));
        }

        private PartyParticipant GetOrCreateParticipant(
            string partyId,
            SessionInfo session,
            string playSessionId = null)
        {
            var user = session.UserName ?? session.UserId;
            if (_plugin.PartyParticipants.TryGetSession(partyId, session.Id, out var existing))
            {
                existing.UserName = user;
                existing.SessionId = session.Id;
                if (!string.IsNullOrEmpty(playSessionId))
                {
                    existing.PlaySessionId = playSessionId;
                }
                return existing;
            }

            _logger.Info($"[Party {partyId}] New participant session: {user} ({session.UserId}) session={session.Id}");
            var participant = new PartyParticipant
            {
                UserId = session.UserId,
                UserName = user,
                SessionId = session.Id,
                PlaySessionId = playSessionId
            };
            _plugin.PartyParticipants.AddOrUpdate(partyId, session.Id, participant);
            return participant;
        }

        private void ResetCommandStateForNewPlayback(
            string partyId,
            SessionInfo session,
            string playSessionId)
        {
            _plugin.PartyParticipants.TryGetSession(
                partyId,
                session.Id,
                out var existingParticipant);
            if (_playbackSyncCoordinator.ResetForNewPlayback(
                    session.Id,
                    existingParticipant?.PlaySessionId,
                    playSessionId))
            {
                _logger.Debug(
                    $"[Party {partyId}] Cleared prior command state for new playback " +
                    $"{playSessionId} on session {session.Id}");
            }
        }

        private void UpdateParticipantActivity(string partyId, string sessionId, long positionTicks, bool isPaused)
        {
            if (_plugin.PartyParticipants.TryGetSession(partyId, sessionId, out var participant))
            {
                participant.LastActivityAt = DateTime.UtcNow;
                participant.CurrentPositionTicks = positionTicks;
                participant.IsPaused = isPaused;
            }
        }

        private bool RemoveParticipant(
            string partyId,
            string sessionId,
            string expectedPlaySessionId = null,
            bool requirePlaySessionMatch = false)
        {
            PartyParticipant participant;
            bool wasMaster;
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
                _playbackSyncCoordinator.ClearSession(sessionId);
                _logger.Info($"[Party {partyId}] Participant session left: {participant.UserName} ({sessionId})");
                return wasMaster;
            }

            return false;
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
                || !string.Equals(session.UserId, party.MasterUserId, StringComparison.Ordinal))
            {
                return false;
            }

            GetOrCreateParticipant(party.Id, session);

            var registeredMasterSessionId = _plugin.PartyParticipants.GetMasterSession(party.Id);
            if (!string.IsNullOrEmpty(registeredMasterSessionId)
                && !string.Equals(registeredMasterSessionId, session.Id, StringComparison.Ordinal))
            {
                // If the registered master session is no longer actively playing
                // (e.g. the client quit without a clean stop event), let the new
                // session of the master user take over instead of being treated
                // as a regular participant.
                var registeredMasterIsActive = _sessionManager.Sessions.Any(s =>
                    string.Equals(s.Id, registeredMasterSessionId, StringComparison.Ordinal)
                    && s.NowPlayingItem != null);
                if (registeredMasterIsActive)
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
                    && s.NowPlayingItem != null);
        }

        private bool CanUserJoinParty(WatchPartyItem party, string userId)
        {
            if (party.AllowedUserIds != null && party.AllowedUserIds.Count > 0)
            {
                if (!party.AllowedUserIds.Contains(userId))
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
                var removedMaster = RemoveParticipant(party.Id, participant.SessionId);

                if (_partySyncedSessions.TryGetValue(party.Id, out var syncedSessions))
                {
                    syncedSessions.TryRemove(participant.SessionId, out _);
                }
                if (_partySessionPauseState.TryGetValue(party.Id, out var pauseStates))
                {
                    pauseStates.TryRemove(participant.SessionId, out _);
                }

                if (removedMaster
                    && _plugin.PartyParticipants.PromoteLatestSessionForUser(
                        party.Id,
                        participant.UserId,
                        out var promotedSessionId))
                {
                    _logger.Info($"[Party {party.Id}] Promoted session {promotedSessionId} as master after inactive session {participant.SessionId} was removed");
                }

                if (!_plugin.PartyParticipants.HasUser(party.Id, participant.UserId)
                    && _plugin.PartyReadyUsers.ContainsKey(party.Id))
                {
                    _plugin.PartyReadyUsers[party.Id].Remove(participant.UserId);
                }
            }
        }

        private List<PartyParticipant> GetParticipants(string partyId)
        {
            return _plugin.PartyParticipants.GetSessions(partyId).ToList();
        }

        private async Task CheckWaitingRoomReadiness(WatchPartyItem party)
        {
            if (!party.IsWaitingRoom || party.IsPlaying)
            {
                _logger.Debug($"[Party {party.Id}] Skipping waiting room check - IsWaitingRoom: {party.IsWaitingRoom}, IsPlaying: {party.IsPlaying}");
                return;
            }

            if (!_plugin.PartyReadyUsers.ContainsKey(party.Id))
            {
                _plugin.PartyReadyUsers[party.Id] = new HashSet<string>();
            }

            var readyCount = _plugin.PartyReadyUsers[party.Id].Count;
            
            int expectedUsers;
            if (party.AllowedUserIds != null && party.AllowedUserIds.Count > 0)
            {
                expectedUsers = party.AllowedUserIds.Count;
                _logger.Info($"[Party {party.Id}] Waiting room: {readyCount}/{expectedUsers} allowed users ready");
            }
            else
            {
                expectedUsers = _plugin.PartyParticipants.DistinctUserCount(party.Id);
                _logger.Info($"[Party {party.Id}] Waiting room: {readyCount}/{expectedUsers} participants ready");
            }

            _logger.Info($"[Party {party.Id}] AutoStartWhenReady={party.AutoStartWhenReady}, MinReadyCount={party.MinReadyCount}, readyCount={readyCount}, expectedUsers={expectedUsers}");

            if (party.AutoStartWhenReady && readyCount >= party.MinReadyCount)
            {
                if (party.AllowedUserIds != null && party.AllowedUserIds.Count > 0)
                {
                    _logger.Info($"[Party {party.Id}] Checking whitelist readiness: {readyCount} >= {party.AllowedUserIds.Count}");
                    if (readyCount >= party.AllowedUserIds.Count)
                    {
                        _logger.Info($"[Party {party.Id}] All whitelisted users ready, starting party!");
                        await StartPartyFromWaitingRoom(party);
                    }
                }
                else if (expectedUsers > 0 && readyCount >= expectedUsers)
                {
                    _logger.Info($"[Party {party.Id}] All participants ready ({readyCount}/{expectedUsers}), starting party!");
                    await StartPartyFromWaitingRoom(party);
                }
                else
                {
                    _logger.Info($"[Party {party.Id}] Not all participants ready yet: {readyCount}/{expectedUsers}");
                }
            }
            else
            {
                _logger.Info($"[Party {party.Id}] Auto-start condition not met - AutoStartWhenReady={party.AutoStartWhenReady}, readyCount ({readyCount}) >= MinReadyCount ({party.MinReadyCount}): {readyCount >= party.MinReadyCount}");
            }
        }

        private async Task StartPartyFromWaitingRoom(WatchPartyItem party)
        {
            party.IsWaitingRoom = false;
            party.IsPlaying = true;
            _playbackSyncCoordinator.UpdateMasterPosition(
                party.Id,
                party.CurrentPositionTicks,
                true,
                DateTime.UtcNow,
                TimeSpan.FromSeconds(party.SyncToleranceSeconds).Ticks);
            _plugin.SaveConfiguration();

            var sessions = _sessionManager.Sessions.Where(s => s.NowPlayingItem != null).ToList();
            foreach (var session in sessions)
            {
                if (_plugin.PartyParticipants.HasUser(party.Id, session.UserId))
                {
                    try
                    {
                        await SendPauseStateCommand(session, false);
                        _logger.Info($"[Party {party.Id}] Started playback for {session.UserName}");
                    }
                    catch (Exception ex)
                    {
                        _logger.ErrorException($"[Party {party.Id}] Error starting playback for {session.UserName}", ex);
                    }
                }
            }
        }

        private async Task HandlePauseAttempt(
            WatchPartyItem party,
            SessionInfo session,
            bool isMaster,
            long? reportedPositionTicks)
        {
            if (party.PauseControl == "Anyone")
            {
                _logger.Info($"[Party {party.Id}] {session.UserName} paused (Anyone mode), pausing all other users");
                
                // Pause all other users in the party
                await PauseAllUsers(party, session.Id);
                await SyncParticipantsAfterPause(party, session, isMaster, reportedPositionTicks);
                return;
            }
            
            if (party.PauseControl == "Host" && !isMaster)
            {
                _logger.Warn($"[Party {party.Id}] {session.UserName} tried to pause (Host-only mode), unpausing");
                await SendPauseStateCommand(session, false);
                return;
            }
            
            if (party.PauseControl == "Host" && isMaster)
            {
                _logger.Info($"[Party {party.Id}] Host {session.UserName} paused, pausing all other users");
                await PauseAllUsers(party, session.Id);
                await SyncParticipantsAfterPause(party, session, true, reportedPositionTicks);
                return;
            }
            
            if (party.PauseControl == "Vote")
            {
                var pauseVotesByUser = _partyPauseVotes.GetOrAdd(
                    party.Id,
                    _ => new ConcurrentDictionary<string, int>());
                pauseVotesByUser[session.UserId] = 1;
                
                var totalParticipants = _plugin.PartyParticipants.DistinctUserCount(party.Id) > 0
                    ? _plugin.PartyParticipants.DistinctUserCount(party.Id)
                    : 1;
                var pauseVotes = pauseVotesByUser.Count;
                var requiredVotes = (int)Math.Ceiling(totalParticipants / 2.0);
                
                _logger.Info($"[Party {party.Id}] Pause vote: {pauseVotes}/{requiredVotes} votes (total: {totalParticipants})");
                
                if (pauseVotes >= requiredVotes)
                {
                    _logger.Info($"[Party {party.Id}] Pause vote passed, pausing all users");
                    await PauseAllUsers(party, null);
                    await SyncParticipantsAfterPause(party, session, isMaster, reportedPositionTicks);
                }
                else
                {
                    _logger.Info($"[Party {party.Id}] Not enough votes, unpausing {session.UserName}");
                    await SendPauseStateCommand(session, false);
                }
            }
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
            try
            {
                var sessions = _sessionManager.Sessions.Where(s => s.NowPlayingItem != null).ToList();
                
                foreach (var otherSession in sessions)
                {
                    // Skip the session that initiated the pause
                    if (otherSession.Id == excludeSessionId)
                    {
                        continue;
                    }
                    
                    var item = _libraryManager.GetItemById(otherSession.NowPlayingItem.Id);
                    var matchingParty = FindPartyForItem(item);
                    
                    if (matchingParty != null && matchingParty.Id == party.Id)
                    {
                        _logger.Info($"[Party {party.Id}] Pausing user {otherSession.UserName} (Session: {otherSession.Id})");
                        try
                        {
                            await SendPauseStateCommand(otherSession, true);
                        }
                        catch (Exception ex)
                        {
                            _logger.ErrorException($"[Party {party.Id}] Error pausing user {otherSession.UserName}", ex);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"[Party {party.Id}] Error pausing all users", ex);
            }
        }
        
        private async Task HandleUnpauseAttempt(WatchPartyItem party, SessionInfo session, bool isHost)
        {
            if (party.PauseControl == "Anyone")
            {
                _logger.Info($"[Party {party.Id}] {session.UserName} unpaused (Anyone mode), unpausing all other users");
                await UnpauseAllUsers(party, session.Id);
                return;
            }
            
            if (party.PauseControl == "Host" && isHost)
            {
                _logger.Info($"[Party {party.Id}] Host {session.UserName} unpaused, unpausing all other users");
                await UnpauseAllUsers(party, session.Id);
                return;
            }
            
            if (party.PauseControl == "Host" && !isHost)
            {
                // Non-host tried to unpause in Host-only mode, re-pause them
                _logger.Warn($"[Party {party.Id}] {session.UserName} tried to unpause (Host-only mode), re-pausing");
                await SendPauseStateCommand(session, true);
                return;
            }
            
            if (party.PauseControl == "Vote")
            {
                // Clear pause votes when someone unpauses
                if (_partyPauseVotes.ContainsKey(party.Id))
                {
                    _partyPauseVotes[party.Id].Clear();
                }
                
                _logger.Info($"[Party {party.Id}] {session.UserName} unpaused, clearing pause votes and unpausing all users");
                await UnpauseAllUsers(party, session.Id);
            }
        }
        
        private async Task UnpauseAllUsers(WatchPartyItem party, string excludeSessionId)
        {
            try
            {
                var sessions = _sessionManager.Sessions.Where(s => s.NowPlayingItem != null).ToList();
                
                foreach (var otherSession in sessions)
                {
                    // Skip the session that initiated the unpause
                    if (otherSession.Id == excludeSessionId)
                    {
                        continue;
                    }
                    
                    var item = _libraryManager.GetItemById(otherSession.NowPlayingItem.Id);
                    var matchingParty = FindPartyForItem(item);
                    
                    if (matchingParty != null && matchingParty.Id == party.Id)
                    {
                        _logger.Info($"[Party {party.Id}] Unpausing user {otherSession.UserName} (Session: {otherSession.Id})");
                        try
                        {
                            await SendPauseStateCommand(otherSession, false);
                        }
                        catch (Exception ex)
                        {
                            _logger.ErrorException($"[Party {party.Id}] Error unpausing user {otherSession.UserName}", ex);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"[Party {party.Id}] Error unpausing all users", ex);
            }
        }

        private async Task SendPauseStateCommand(SessionInfo session, bool isPaused)
        {
            if (session == null
                || !PlaybackControlCapabilities.CanReceivePauseState(
                    session.Client,
                    session.SupportsRemoteControl))
            {
                _logger.Info(
                    $"[Watch Party] Session {session?.Id} ({session?.Client}) does not " +
                    $"advertise pause control, skipping {(isPaused ? "pause" : "unpause")}");
                return;
            }

            _playbackSyncCoordinator.ExpectPauseState(session.Id, isPaused, DateTime.UtcNow);
            await _sessionManager.SendPlaystateCommand(
                session.Id,
                session.Id,
                new PlaystateRequest
                {
                    Command = isPaused ? PlaystateCommand.Pause : PlaystateCommand.Unpause
                },
                CancellationToken.None);
        }

        private void ValidateAndCleanWatchParties(bool force = false)
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
                    _plugin.SaveConfiguration();
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error validating watch parties", ex);
            }
        }

        private void CleanupRemovedParties()
        {
            try
            {
                var config = _plugin.Configuration;
                var currentPartyIds = new HashSet<string>(config.WatchParties.Select(p => p.Id));
                var removedPartyIds = _trackedPartyIds.Except(currentPartyIds).ToList();

                foreach (var removedId in removedPartyIds)
                {
                    _logger.Info($"Party {removedId} was removed, cleaning up...");
                    _playbackSyncCoordinator.ClearParty(removedId);

                    _partySyncedSessions.TryRemove(removedId, out _);
                    _partySessionPauseState.TryRemove(removedId, out _);
                    _partyHostSessions.Remove(removedId);
                    _partyPauseVotes.TryRemove(removedId, out _);
                    _lastProgressCheckpoint.TryRemove(removedId, out _);
                    _seriesSelectionVersions.TryRemove(removedId, out _);
                    _plugin.PartyParticipants.ClearParty(removedId);
                    if (_plugin.PartyReadyUsers.ContainsKey(removedId))
                    {
                        _plugin.PartyReadyUsers[removedId].Clear();
                    }
                }

                _trackedPartyIds.Clear();
                _trackedPartyIds.UnionWith(currentPartyIds);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error cleaning up removed parties", ex);
            }
        }

        private async void OnPlaybackStart(object sender, PlaybackProgressEventArgs e)
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
                        }, CancellationToken.None);
                        return;
                    }

                    var nowUtc = DateTime.UtcNow;
                    var userStartPosition = e.PlaybackPositionTicks ?? 0;
                    ResetCommandStateForNewPlayback(
                        party.Id,
                        e.Session,
                        e.PlaySessionId);
                    GetOrCreateParticipant(party.Id, e.Session, e.PlaySessionId);
                    var isMaster = TryResolveMasterSession(party, e.Session);
                    var startedEpisodeId = FindSeriesEpisodeId(party, e.Item);
                    var currentEpisode = SeriesPartyQueue.GetCurrentEpisode(party);
                    var episodeSwitchTarget = GetSeriesEpisodeSwitchTarget(party, e.Item);
                    var isExpectedSeriesStart = _seriesEpisodeTransitions.ConsumeExpectedStart(
                        e.Session.Id,
                        startedEpisodeId,
                        nowUtc);
                    long? masterSelectionVersion = null;
                    if (party.IsSeriesParty
                        && isMaster
                        && !isExpectedSeriesStart
                        && !string.IsNullOrEmpty(startedEpisodeId))
                    {
                        masterSelectionVersion = _seriesSelectionVersions.AddOrUpdate(
                            party.Id,
                            1,
                            (_, currentVersion) => currentVersion + 1);
                    }

                    if (party.IsSeriesParty
                        && !string.IsNullOrEmpty(startedEpisodeId)
                        && currentEpisode != null
                        && (masterSelectionVersion.HasValue
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

                        var transitionWasQueued = await EnterSeriesTransitionAsync(party.Id);
                        try
                        {
                            if (!_seriesSelectionVersions.TryGetValue(party.Id, out var latestSelectionVersion)
                                || latestSelectionVersion != masterSelectionVersion.Value)
                            {
                                _logger.Debug(
                                    $"[Party {party.Id}] Skipping superseded episode selection {startedEpisodeId}");
                                return;
                            }

                            currentEpisode = SeriesPartyQueue.GetCurrentEpisode(party);
                            if (currentEpisode != null
                                && !string.Equals(startedEpisodeId, currentEpisode.ItemId, StringComparison.OrdinalIgnoreCase))
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

                                ResetSeriesEpisodeSyncState(party);
                                party.CurrentPositionTicks = userStartPosition;
                                party.IsPlaying = !party.IsWaitingRoom && !e.IsPaused;
                                _playbackSyncCoordinator.UpdateMasterPosition(
                                    party.Id,
                                    userStartPosition,
                                    party.IsPlaying,
                                    nowUtc,
                                    TimeSpan.FromSeconds(party.SyncToleranceSeconds).Ticks);
                                _plugin.SaveConfiguration();
                                _lastProgressCheckpoint[party.Id] = nowUtc;

                                _logger.Info(
                                    $"[Party {party.Id}] Master selected queued episode {startedEpisodeId}; " +
                                    $"switching {transitionSessions.Count} participant session(s)");
                                await PlaySeriesEpisodeForSessions(
                                    party,
                                    SeriesPartyQueue.GetCurrentEpisode(party),
                                    transitionSessions,
                                    userStartPosition);
                            }
                        }
                        finally
                        {
                            ExitSeriesTransition(party.Id);
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
                    }
                    
                    if (party.IsWaitingRoom && !party.IsPlaying)
                    {
                        _logger.Info($"[Party {party.Id}] User {e.Session.UserId} started playback - marking as ready");
                        
                        if (!_plugin.PartyReadyUsers.ContainsKey(party.Id))
                        {
                            _plugin.PartyReadyUsers[party.Id] = new HashSet<string>();
                        }
                        _plugin.PartyReadyUsers[party.Id].Add(e.Session.UserId);
                        
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
            catch (Exception ex)
            {
                _logger.ErrorException("[Watch Party] Error handling playback start", ex);
            }
        }

        private void CheckAndSyncUsers(object state)
        {
            try
            {
                ValidateAndCleanWatchParties();
                
                var config = _plugin.Configuration;
                
                foreach (var party in config.WatchParties.Where(p => p.IsActive))
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
                            var currentPosition = session.PlayState?.PositionTicks ?? 0;
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
                                        Task.Run(() => PlaySeriesEpisodeForSessions(
                                            party,
                                            episodeSwitchTarget,
                                            new[] { session },
                                            estimatedPartyPosition));
                                    }
                                    continue;
                                }
                            }

                            UpdateParticipantActivity(party.Id, session.Id, currentPosition, session.PlayState?.IsPaused ?? false);
                            
                            if (!isMaster
                                && positionDifference > maxBufferThreshold
                                && currentPosition < estimatedPartyPosition)
                            {
                                _logger.Warn($"[Party {party.Id}] Session {session.Id} is {TimeSpan.FromTicks(positionDifference).TotalSeconds:F1}s behind (exceeds buffer threshold)");
                                
                                if (_plugin.PartyParticipants.TryGetSession(party.Id, session.Id, out var participant))
                                {
                                    participant.IsBuffering = true;
                                }
                            }
                            else if (_plugin.PartyParticipants.TryGetSession(party.Id, session.Id, out var inSyncParticipant))
                            {
                                inSyncParticipant.IsBuffering = false;
                            }

                            if (!isMaster)
                            {
                                if (positionDifference <= PeriodicSyncTolerance.Ticks)
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
                                Task.Run(async () => await SyncUserToPosition(
                                    session,
                                    item,
                                    estimatedPartyPosition,
                                    controllingSession: controllingSession));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[Watch Party] Error in periodic sync check", ex);
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

                if (!session.SupportsRemoteControl)
                {
                    _logger.Info($"[Watch Party] Session {session.Id} ({session.Client}) does not support remote control, skipping seek to {TimeSpan.FromTicks(positionTicks).TotalSeconds:F1}s");
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
                
                await _sessionManager.SendPlaystateCommand(
                    controllingSessionId,
                    session.Id,
                    new PlaystateRequest
                    {
                        Command = PlaystateCommand.Seek,
                        SeekPositionTicks = adjustedPosition,
                        ControllingUserId = controllingSession?.UserId
                    },
                    CancellationToken.None);
                    
                _logger.Info($"[Watch Party] Server accepted seek command for session {session.Id} (client confirmation pending)");
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"[Watch Party] Error syncing session {session.Id}", ex);
            }
        }

        private WatchPartyItem FindPartyForItem(BaseItem item)
        {
            return item == null
                ? null
                : WatchPartyItemMatcher.FindActiveParty(
                    _plugin.Configuration.WatchParties,
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

        private async void OnPlaybackProgress(object sender, PlaybackProgressEventArgs e)
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
                    if (!_plugin.PartyParticipants.IsCurrentPlaybackSession(
                            party.Id,
                            e.Session.Id,
                            e.PlaySessionId)
                        && !isExpectedSeriesProgress)
                    {
                        _logger.Debug(
                            $"[Party {party.Id}] Ignoring stale progress from playback " +
                            $"{e.PlaySessionId} for session {e.Session.Id}");
                        return;
                    }

                    ResetCommandStateForNewPlayback(
                        party.Id,
                        e.Session,
                        e.PlaySessionId);

                    var pauseState = _partySessionPauseState.GetOrAdd(
                        party.Id,
                        _ => new ConcurrentDictionary<string, bool>());
                    var wasPaused = pauseState.TryGetValue(e.Session.Id, out var previousPause) && previousPause;
                    var reportedPosition = e.PlaybackPositionTicks;
                    var currentPosition = reportedPosition ?? 0;
                    var isMaster = TryResolveMasterSession(party, e.Session);
                    if (isMaster)
                    {
                        GetOrCreateParticipant(party.Id, e.Session, e.PlaySessionId);
                    }

                    UpdateParticipantActivity(party.Id, e.Session.Id, currentPosition, e.IsPaused);

                    // Emby only fires PlaybackStart once per PlaySessionId. When a client
                    // resumes party content (or restarts playback) with the same play session
                    // within the idle window, no PlaybackStart event arrives, so the session
                    // never rejoins the synced set and drifts until the next periodic pass.
                    // Re-register on the first progress report and calibrate immediately so
                    // the participant lands on the party clock within a second or two instead
                    // of playing at a stale position for 10-30 seconds.
                    if (!isMaster && !party.IsWaitingRoom && CanUserJoinParty(party, e.Session.UserId))
                    {
                        GetOrCreateParticipant(party.Id, e.Session, e.PlaySessionId);

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

                    var isPauseTransition = e.IsPaused != wasPaused;
                    var isExpectedPauseEcho = _playbackSyncCoordinator.ConsumeExpectedPauseState(
                        e.Session.Id,
                        e.IsPaused,
                        nowUtc,
                        clearOnMismatch: isPauseTransition);
                    var isSeekStateEchoExpected = _playbackSyncCoordinator.IsSeekStateEchoExpected(
                        e.Session.Id,
                        nowUtc);

                    if (isPauseTransition && (isExpectedPauseEcho || isSeekStateEchoExpected))
                    {
                        _logger.Debug(
                            $"[Party {party.Id}] Ignoring playback-state echo from session {e.Session.Id} " +
                            $"(expected: {isExpectedPauseEcho}, seek state echo: {isSeekStateEchoExpected})");
                    }
                    else if (e.IsPaused && !wasPaused)
                    {
                        await HandlePauseAttempt(
                            party,
                            e.Session,
                            isMaster,
                            reportedPosition);
                    }
                    else if (!e.IsPaused && wasPaused)
                    {
                        await HandleUnpauseAttempt(party, e.Session, isMaster);
                    }

                    pauseState[e.Session.Id] = e.IsPaused;

                    if (isMaster)
                    {
                        if (!party.IsWaitingRoom)
                        {
                            party.IsPlaying = !e.IsPaused;
                        }

                        if (reportedPosition.HasValue)
                        {
                            var priorPartyPosition = party.CurrentPositionTicks;
                            var masterSeeked = _playbackSyncCoordinator.UpdateMasterPosition(
                                party.Id,
                                currentPosition,
                                !e.IsPaused && !party.IsWaitingRoom,
                                nowUtc,
                                TimeSpan.FromSeconds(party.SyncToleranceSeconds).Ticks);

                            party.CurrentPositionTicks = currentPosition;

                            _logger.Debug($"[Watch Party] Master user updated party {party.Id} position to {party.CurrentPositionTicks} ticks, Playing: {party.IsPlaying}");
                            CheckpointPartyProgress(party);

                            // A transition into pause already performed one explicit,
                            // offset-free sync above. Do not classify the same pause report
                            // as a second seek. Timeline drags while already paused still sync.
                            if (masterSeeked && !(e.IsPaused && !wasPaused))
                            {
                                _logger.Info(
                                    $"[Party {party.Id}] Master seeked to " +
                                    $"{TimeSpan.FromTicks(currentPosition).TotalSeconds:F1}s; scheduling participant sync");
                                ScheduleMasterSeekSync(
                                    party,
                                    e.Session.Id,
                                    currentPosition,
                                    priorPartyPosition);
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
            long priorPartyPositionTicks)
        {
            var debounce = GetMasterSeekDebounce(priorPartyPositionTicks, positionTicks);

            lock (_masterSeekSyncLock)
            {
                if (_pendingMasterSeeks.TryGetValue(party.Id, out var pending))
                {
                    var newIsDrastic = debounce > MasterSeekDebounce;
                    var pendingIsDrastic = pending.Debounce > MasterSeekDebounce;
                    if (newIsDrastic && pendingIsDrastic)
                    {
                        // Both reports are transient near-zero positions from the same
                        // stream reload: keep the original hold deadline and just track
                        // the latest reported position. Restarting the window on every
                        // reload report would defer the sync indefinitely.
                        pending.MasterSessionId = masterSessionId;
                        pending.PositionTicks = positionTicks;
                        return;
                    }

                    // A different class of seek: replace the target and restart the
                    // debounce window so the participant receives one final seek once
                    // the drag settles instead of a burst of intermediate positions.
                    pending.MasterSessionId = masterSessionId;
                    pending.PositionTicks = positionTicks;
                    pending.Debounce = debounce;
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
                    WindowCts = new CancellationTokenSource()
                };
                _pendingMasterSeeks[party.Id] = pending;
                _ = CompleteMasterSeekSyncAsync(party, pending, pending.WindowCts.Token);
            }
        }

        private static TimeSpan GetMasterSeekDebounce(long priorPositionTicks, long positionTicks)
        {
            // Emby Web resets its video element to ~1s while reloading the stream after a
            // seek. If the master was well ahead and suddenly reports a near-zero position,
            // hold for the reload to settle instead of bouncing participants to 0 and back.
            var looksLikeReloadArtifact = positionTicks <= MasterSeekNearZeroThreshold.Ticks
                && priorPositionTicks > MasterSeekDrasticFromThreshold.Ticks;
            return looksLikeReloadArtifact ? MasterSeekDrasticDebounce : MasterSeekDebounce;
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
            lock (_masterSeekSyncLock)
            {
                if (!_pendingMasterSeeks.TryGetValue(party.Id, out var current)
                    || !ReferenceEquals(current, pending))
                {
                    return;
                }

                _pendingMasterSeeks.Remove(party.Id);
                masterSessionId = pending.MasterSessionId;
            }

            // Sync to where the master clock projects to at fire time, so a near-zero
            // hold that expires without a real target still lands participants at the
            // master's actual (advanced) position.
            positionTicks = _playbackSyncCoordinator.GetEstimatedPartyPosition(
                party.Id,
                pending.PositionTicks,
                DateTime.UtcNow);

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
                if (matchingParty != null && matchingParty.Id == party.Id)
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
            _plugin.SaveConfiguration();
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
                && session.SupportsRemoteControl
                && session.PlayableMediaTypes != null
                && session.PlayableMediaTypes.Contains("Video", StringComparer.OrdinalIgnoreCase);
        }

        private async Task<bool> EnterSeriesTransitionAsync(string partyId)
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
                await Task.Delay(25).ConfigureAwait(false);
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
            long startPositionTicks = 0)
        {
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
                        CancellationToken.None);
                    _logger.Info($"[Party {party.Id}] Sent episode {episode.ItemName} to {session.UserName}");
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

            _partyPauseVotes.TryRemove(party.Id, out _);
            _playbackSyncCoordinator.ClearParty(party.Id);
            foreach (var participant in _plugin.PartyParticipants.GetSessions(party.Id))
            {
                participant.CurrentPositionTicks = 0;
                participant.IsPaused = false;
                participant.IsBuffering = false;
            }
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

        private async void OnPlaybackStopped(object sender, PlaybackStopEventArgs e)
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

                                _plugin.SaveConfiguration();
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
                            _plugin.SaveConfiguration();
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

                    RemoveParticipant(
                        party.Id,
                        e.Session.Id,
                        e.PlaySessionId,
                        requirePlaySessionMatch: true);
                    
                    if (isMaster)
                    {
                        // The same user may still be watching in another session. Promote that
                        // session instead of stopping the party when the old master session ends.
                        if (_plugin.PartyParticipants.PromoteLatestSessionForUser(
                                party.Id,
                                e.Session.UserId,
                                out var promotedMasterSessionId))
                        {
                            _logger.Info($"[Watch Party] Promoted session {promotedMasterSessionId} as master for party {party.Id} after session {e.Session.Id} stopped");
                            if (_partySyncedSessions.ContainsKey(party.Id))
                            {
                                _partySyncedSessions[party.Id].TryRemove(e.Session.Id, out _);
                            }
                            if (_partySessionPauseState.ContainsKey(party.Id))
                            {
                                _partySessionPauseState[party.Id].TryRemove(e.Session.Id, out _);
                            }
                            CancelPendingMasterSeekSync(party.Id);
                            return;
                        }

                        _logger.Info($"[Watch Party] Master user {e.Session.UserId} stopped for party {party.Id} (keeping position)");
                        party.IsPlaying = false;
                        _playbackSyncCoordinator.StopMasterClock(
                            party.Id,
                            stoppedPosition,
                            DateTime.UtcNow);
                        CancelPendingMasterSeekSync(party.Id);
                        // Reset waiting room so it re-engages for the next viewing
                        if (!party.IsWaitingRoom && party.MinReadyCount > 1)
                        {
                            party.IsWaitingRoom = true;
                            _logger.Info($"[Party {party.Id}] Re-enabled waiting room (MinReadyCount={party.MinReadyCount})");
                        }
                        _plugin.SaveConfiguration();
                        
                        if (_partySyncedSessions.ContainsKey(party.Id))
                        {
                            _partySyncedSessions[party.Id].Clear();
                        }
                        if (_partySessionPauseState.ContainsKey(party.Id))
                        {
                            _partySessionPauseState[party.Id].Clear();
                        }
                        if (_plugin.PartyReadyUsers.ContainsKey(party.Id))
                        {
                            _plugin.PartyReadyUsers[party.Id].Clear();
                        }
                        
                        var remainingParticipants = _plugin.PartyParticipants.GetSessions(party.Id);
                        if (remainingParticipants.Count > 0)
                        {
                            var newHostParticipant = remainingParticipants
                                .OrderByDescending(p => p.LastActivityAt)
                                .First();
                            party.HostUserId = newHostParticipant.UserId;
                            _partyHostSessions[party.Id] = newHostParticipant.SessionId;
                            _logger.Info($"[Watch Party] Assigned new host: {newHostParticipant.UserName}");
                            _plugin.SaveConfiguration();
                        }
                    }
                    else
                    {
                        _logger.Info($"[Watch Party] Participant {e.Session.UserId} stopped watching party content");
                        
                        if (_partySyncedSessions.ContainsKey(party.Id))
                        {
                            _partySyncedSessions[party.Id].TryRemove(e.Session.Id, out _);
                        }
                        if (_partySessionPauseState.ContainsKey(party.Id))
                        {
                            _partySessionPauseState[party.Id].TryRemove(e.Session.Id, out _);
                        }
                        if (!_plugin.PartyParticipants.HasUser(party.Id, e.Session.UserId)
                            && _plugin.PartyReadyUsers.ContainsKey(party.Id))
                        {
                            _plugin.PartyReadyUsers[party.Id].Remove(e.Session.UserId);
                        }
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
            
            _syncTimer?.Dispose();

            try
            {
                _plugin.SaveConfiguration();
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
            public CancellationTokenSource WindowCts { get; set; }
        }
    }
}
