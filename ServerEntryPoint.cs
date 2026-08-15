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
        private readonly ConcurrentDictionary<string, DateTime> _expectedSeriesEpisodeStarts = new ConcurrentDictionary<string, DateTime>();
        private readonly object _seriesTransitionLock = new object();
        private readonly ConcurrentDictionary<string, DateTime> _lastProgressCheckpoint = new ConcurrentDictionary<string, DateTime>();
        private readonly PlaybackSyncCoordinator _playbackSyncCoordinator = new PlaybackSyncCoordinator();
        private static readonly TimeSpan ProgressCheckpointInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan PartyValidationInterval = TimeSpan.FromMinutes(5);
        private DateTime _lastPartyValidationUtc = DateTime.MinValue;

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

        private PartyParticipant GetOrCreateParticipant(string partyId, SessionInfo session)
        {
            var participants = _plugin.PartyParticipants.GetOrAdd(
                partyId,
                _ => new ConcurrentDictionary<string, PartyParticipant>());
            var user = session.UserName ?? session.UserId;
            var participant = participants.GetOrAdd(
                session.UserId,
                _ =>
                {
                    _logger.Info($"[Party {partyId}] New participant: {user} ({session.UserId})");
                    return new PartyParticipant
                    {
                        UserId = session.UserId,
                        UserName = user,
                        SessionId = session.Id
                    };
                });

            participant.SessionId = session.Id;
            return participant;
        }

        private void UpdateParticipantActivity(string partyId, string userId, long positionTicks, bool isPaused)
        {
            if (_plugin.PartyParticipants.ContainsKey(partyId) && _plugin.PartyParticipants[partyId].ContainsKey(userId))
            {
                var participant = _plugin.PartyParticipants[partyId][userId];
                participant.LastActivityAt = DateTime.UtcNow;
                participant.CurrentPositionTicks = positionTicks;
                participant.IsPaused = isPaused;
            }
        }

        private void RemoveParticipant(string partyId, string userId)
        {
            if (_plugin.PartyParticipants.ContainsKey(partyId) && _plugin.PartyParticipants[partyId].ContainsKey(userId))
            {
                var participant = _plugin.PartyParticipants[partyId][userId];
                _logger.Info($"[Party {partyId}] Participant left: {participant.UserName}");
                _plugin.PartyParticipants[partyId].TryRemove(userId, out _);
            }
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

            if (_plugin.PartyParticipants.ContainsKey(party.Id)
                && _plugin.PartyParticipants[party.Id].ContainsKey(userId))
            {
                return true;
            }

            if (_plugin.PartyParticipants.ContainsKey(party.Id))
            {
                var currentCount = _plugin.PartyParticipants[party.Id].Count;
                if (currentCount >= party.MaxParticipants)
                {
                    _logger.Warn($"[Party {party.Id}] Maximum participants ({party.MaxParticipants}) reached");
                    return false;
                }
            }

            return true;
        }

        private void CheckAndRemoveInactiveParticipants(WatchPartyItem party)
        {
            if (!party.AutoKickInactiveMinutes || !_plugin.PartyParticipants.ContainsKey(party.Id))
            {
                return;
            }

            var inactiveThreshold = DateTime.UtcNow.AddMinutes(-party.InactiveTimeoutMinutes);
            var inactiveUsers = _plugin.PartyParticipants[party.Id]
                .Where(kvp => kvp.Value.LastActivityAt < inactiveThreshold)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var userId in inactiveUsers)
            {
                var participant = _plugin.PartyParticipants[party.Id][userId];
                _logger.Info($"[Party {party.Id}] Removing inactive user: {participant.UserName} (inactive for {party.InactiveTimeoutMinutes} minutes)");
                RemoveParticipant(party.Id, userId);
                
                if (_partySyncedSessions.TryGetValue(party.Id, out var syncedSessions))
                {
                    foreach (var sessionId in syncedSessions.Keys)
                    {
                        var session = _sessionManager.Sessions.FirstOrDefault(s => s.Id == sessionId);
                        if (session?.UserId == userId)
                        {
                            syncedSessions.TryRemove(sessionId, out _);
                        }
                    }
                }
            }
        }

        private List<PartyParticipant> GetParticipants(string partyId)
        {
            if (_plugin.PartyParticipants.ContainsKey(partyId))
            {
                return _plugin.PartyParticipants[partyId].Values.ToList();
            }
            return new List<PartyParticipant>();
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
                expectedUsers = _plugin.PartyParticipants.ContainsKey(party.Id) ? _plugin.PartyParticipants[party.Id].Count : 0;
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
                if (_plugin.PartyParticipants.ContainsKey(party.Id) && _plugin.PartyParticipants[party.Id].ContainsKey(session.UserId))
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

        private async Task HandlePauseAttempt(WatchPartyItem party, SessionInfo session, bool isHost)
        {
            if (party.PauseControl == "Anyone")
            {
                _logger.Info($"[Party {party.Id}] {session.UserName} paused (Anyone mode), pausing all other users");
                
                // Pause all other users in the party
                await PauseAllUsers(party, session.Id);
                return;
            }
            
            if (party.PauseControl == "Host" && !isHost)
            {
                _logger.Warn($"[Party {party.Id}] {session.UserName} tried to pause (Host-only mode), unpausing");
                await SendPauseStateCommand(session, false);
                return;
            }
            
            if (party.PauseControl == "Host" && isHost)
            {
                _logger.Info($"[Party {party.Id}] Host {session.UserName} paused, pausing all other users");
                await PauseAllUsers(party, session.Id);
                return;
            }
            
            if (party.PauseControl == "Vote")
            {
                var pauseVotesByUser = _partyPauseVotes.GetOrAdd(
                    party.Id,
                    _ => new ConcurrentDictionary<string, int>());
                pauseVotesByUser[session.UserId] = 1;
                
                var totalParticipants = _plugin.PartyParticipants.ContainsKey(party.Id) ? _plugin.PartyParticipants[party.Id].Count : 1;
                var pauseVotes = pauseVotesByUser.Count;
                var requiredVotes = (int)Math.Ceiling(totalParticipants / 2.0);
                
                _logger.Info($"[Party {party.Id}] Pause vote: {pauseVotes}/{requiredVotes} votes (total: {totalParticipants})");
                
                if (pauseVotes >= requiredVotes)
                {
                    _logger.Info($"[Party {party.Id}] Pause vote passed, pausing all users");
                    await PauseAllUsers(party, null);
                }
                else
                {
                    _logger.Info($"[Party {party.Id}] Not enough votes, unpausing {session.UserName}");
                    await SendPauseStateCommand(session, false);
                }
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
                    var isMaster = !string.IsNullOrEmpty(party.MasterUserId) && e.Session.UserId == party.MasterUserId;
                    var startedEpisodeId = FindSeriesEpisodeId(party, e.Item);
                    var currentEpisode = SeriesPartyQueue.GetCurrentEpisode(party);
                    var episodeSwitchTarget = GetSeriesEpisodeSwitchTarget(party, e.Item);
                    var isExpectedSeriesStart = ConsumeExpectedSeriesEpisodeStart(
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
                                            && (string.IsNullOrEmpty(party.MasterUserId) || session.UserId != party.MasterUserId)))
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

                    GetOrCreateParticipant(party.Id, e.Session);
                    UpdateParticipantActivity(party.Id, e.Session.UserId, userStartPosition, e.IsPaused);

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
                        await SyncUserToPosition(e.Session, e.Item, targetPosition);
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

                    if (!party.IsPlaying || party.IsWaitingRoom)
                    {
                        continue;
                    }

                    var nowUtc = DateTime.UtcNow;
                    var estimatedPartyPosition = _playbackSyncCoordinator.GetEstimatedPartyPosition(
                        party.Id,
                        party.CurrentPositionTicks,
                        nowUtc);
                    _logger.Debug($"[Party {party.Id}] Periodic sync check at estimated position {estimatedPartyPosition} ticks");
                    
                    var sessions = _sessionManager.Sessions.Where(s => s.NowPlayingItem != null).ToList();
                    var syncThreshold = TimeSpan.FromSeconds(party.SyncToleranceSeconds).Ticks;
                    var maxBufferThreshold = TimeSpan.FromSeconds(party.MaxBufferThresholdSeconds).Ticks;
                    
                    foreach (var session in sessions)
                    {
                        var item = _libraryManager.GetItemById(session.NowPlayingItem.Id);
                        var matchingParty = FindPartyForItem(item);
                        
                        if (matchingParty != null && matchingParty.Id == party.Id)
                        {
                            var currentPosition = session.PlayState?.PositionTicks ?? 0;
                            var positionDifference = Math.Abs(currentPosition - estimatedPartyPosition);
                            var isMaster = !string.IsNullOrEmpty(party.MasterUserId)
                                && session.UserId == party.MasterUserId;

                            if (party.IsSeriesParty && !isMaster)
                            {
                                var episodeSwitchTarget = GetSeriesEpisodeSwitchTarget(party, item);
                                if (episodeSwitchTarget != null)
                                {
                                    var expectedStartKey = GetExpectedSeriesEpisodeStartKey(
                                        session.Id,
                                        episodeSwitchTarget.ItemId);
                                    if (!_expectedSeriesEpisodeStarts.TryGetValue(expectedStartKey, out var expiresAt)
                                        || expiresAt < nowUtc)
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
                            
                            UpdateParticipantActivity(party.Id, session.UserId, currentPosition, session.PlayState?.IsPaused ?? false);
                            
                            if (!isMaster && positionDifference > maxBufferThreshold && currentPosition < estimatedPartyPosition)
                            {
                                _logger.Warn($"[Party {party.Id}] Session {session.Id} is {TimeSpan.FromTicks(positionDifference).TotalSeconds:F1}s behind (exceeds buffer threshold)");
                                
                                if (_plugin.PartyParticipants.ContainsKey(party.Id) && _plugin.PartyParticipants[party.Id].ContainsKey(session.UserId))
                                {
                                    _plugin.PartyParticipants[party.Id][session.UserId].IsBuffering = true;
                                }
                            }
                            
                            if (PlaybackSyncCoordinator.ShouldSynchronizeParticipant(
                                party.IsActive,
                                party.IsPlaying,
                                party.IsWaitingRoom,
                                isMaster,
                                currentPosition,
                                estimatedPartyPosition,
                                syncThreshold))
                            {
                                _logger.Info($"[Party {party.Id}] Session {session.Id} is {TimeSpan.FromTicks(positionDifference).TotalSeconds:F1}s out of sync, syncing");
                                Task.Run(async () => await SyncUserToPosition(session, item, estimatedPartyPosition));
                            }
                            else
                            {
                                _logger.Debug($"[Party {party.Id}] Session {session.Id} is in sync (diff: {TimeSpan.FromTicks(positionDifference).TotalSeconds:F1}s)");
                                
                                if (_plugin.PartyParticipants.ContainsKey(party.Id) && _plugin.PartyParticipants[party.Id].ContainsKey(session.UserId))
                                {
                                    _plugin.PartyParticipants[party.Id][session.UserId].IsBuffering = false;
                                }
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

        private async Task SyncUserToPosition(SessionInfo session, BaseItem item, long positionTicks)
        {
            try
            {
                var config = _plugin.Configuration;
                
                var offsetTicks = TimeSpan.FromMilliseconds(config.SyncOffsetMilliseconds).Ticks;
                var adjustedPosition = Math.Max(0, positionTicks + offsetTicks);

                if (!_playbackSyncCoordinator.TryBeginSeek(session.Id, adjustedPosition, DateTime.UtcNow))
                {
                    _logger.Debug($"[Watch Party] Suppressed duplicate seek for settling session {session.Id}");
                    return;
                }
                
                _logger.Info($"[Watch Party] Syncing session {session.Id} to position {positionTicks} ticks (adjusted: {adjustedPosition} with {config.SyncOffsetMilliseconds:+#;-#;0}ms offset)");
                
                await _sessionManager.SendPlaystateCommand(
                    session.Id,
                    session.Id,
                    new PlaystateRequest
                    {
                        Command = PlaystateCommand.Seek,
                        SeekPositionTicks = adjustedPosition
                    },
                    CancellationToken.None);
                    
                _logger.Info($"[Watch Party] Seek command sent successfully to session {session.Id}");
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
                    var pauseState = _partySessionPauseState.GetOrAdd(
                        party.Id,
                        _ => new ConcurrentDictionary<string, bool>());
                    var wasPaused = pauseState.TryGetValue(e.Session.Id, out var previousPause) && previousPause;
                    var nowUtc = DateTime.UtcNow;
                    var currentPosition = e.PlaybackPositionTicks ?? 0;
                    var isMaster = !string.IsNullOrEmpty(party.MasterUserId) && e.Session.UserId == party.MasterUserId;

                    UpdateParticipantActivity(party.Id, e.Session.UserId, currentPosition, e.IsPaused);

                    var isPauseTransition = e.IsPaused != wasPaused;
                    var isExpectedPauseEcho = _playbackSyncCoordinator.ConsumeExpectedPauseState(
                        e.Session.Id,
                        e.IsPaused,
                        nowUtc);
                    var isSeekSettling = _playbackSyncCoordinator.IsSeekSettling(e.Session.Id, nowUtc);

                    if (isPauseTransition && (isExpectedPauseEcho || isSeekSettling))
                    {
                        _logger.Debug(
                            $"[Party {party.Id}] Ignoring playback-state echo from session {e.Session.Id} " +
                            $"(expected: {isExpectedPauseEcho}, seek settling: {isSeekSettling})");
                    }
                    else if (e.IsPaused && !wasPaused)
                    {
                        await HandlePauseAttempt(party, e.Session, isMaster);
                    }
                    else if (!e.IsPaused && wasPaused)
                    {
                        await HandleUnpauseAttempt(party, e.Session, isMaster);
                    }

                    pauseState[e.Session.Id] = e.IsPaused;

                    if (isMaster)
                    {
                        var masterSeeked = _playbackSyncCoordinator.UpdateMasterPosition(
                            party.Id,
                            currentPosition,
                            !e.IsPaused && !party.IsWaitingRoom,
                            nowUtc,
                            TimeSpan.FromSeconds(party.SyncToleranceSeconds).Ticks);

                        party.CurrentPositionTicks = currentPosition;
                        if (!party.IsWaitingRoom)
                        {
                            party.IsPlaying = !e.IsPaused;
                        }

                        _logger.Debug($"[Watch Party] Master user updated party {party.Id} position to {party.CurrentPositionTicks} ticks, Playing: {party.IsPlaying}");
                        CheckpointPartyProgress(party);

                        if (masterSeeked && party.IsPlaying)
                        {
                            _logger.Info(
                                $"[Party {party.Id}] Master seeked to " +
                                $"{TimeSpan.FromTicks(currentPosition).TotalSeconds:F1}s; syncing participants once");
                            await SyncParticipantsToPosition(party, e.Session.Id, currentPosition);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[Watch Party] Error handling playback progress", ex);
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
                if (session.Id == masterSessionId
                    || (!string.IsNullOrEmpty(party.MasterUserId) && session.UserId == party.MasterUserId))
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

                    await SyncUserToPosition(session, item, positionTicks);
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

            if (_plugin.PartyParticipants.TryGetValue(party.Id, out var participants))
            {
                foreach (var participant in participants.Values)
                {
                    if (!string.IsNullOrEmpty(participant.SessionId))
                    {
                        sessionIds.Add(participant.SessionId);
                    }
                }
            }

            return _sessionManager.Sessions.Where(session => sessionIds.Contains(session.Id)).ToList();
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
            foreach (var expectedStart in _expectedSeriesEpisodeStarts.Where(entry => entry.Value < nowUtc))
            {
                _expectedSeriesEpisodeStarts.TryRemove(expectedStart.Key, out _);
            }
            var expectedStartExpiration = nowUtc.AddSeconds(30);

            foreach (var session in sessions)
            {
                var expectedStartKey = GetExpectedSeriesEpisodeStartKey(session.Id, episode.ItemId);
                try
                {
                    _expectedSeriesEpisodeStarts[expectedStartKey] = expectedStartExpiration;
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
                    _expectedSeriesEpisodeStarts.TryRemove(expectedStartKey, out _);
                    _logger.ErrorException(
                        $"[Party {party.Id}] Client {session.Client} did not accept episode {episode.ItemName}",
                        ex);
                }
            }
        }

        private static string GetExpectedSeriesEpisodeStartKey(string sessionId, string episodeItemId)
        {
            return sessionId + ":" + episodeItemId;
        }

        private bool ConsumeExpectedSeriesEpisodeStart(
            string sessionId,
            string episodeItemId,
            DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(episodeItemId))
            {
                return false;
            }

            var key = GetExpectedSeriesEpisodeStartKey(sessionId, episodeItemId);
            return _expectedSeriesEpisodeStarts.TryRemove(key, out var expiresAtUtc)
                && expiresAtUtc >= nowUtc;
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
            if (_plugin.PartyParticipants.TryGetValue(party.Id, out var participants))
            {
                foreach (var participant in participants.Values)
                {
                    participant.CurrentPositionTicks = 0;
                    participant.IsPaused = false;
                    participant.IsBuffering = false;
                }
            }
        }

        private long GetLastKnownPosition(WatchPartyItem party, SessionInfo session)
        {
            if (session?.PlayState?.PositionTicks is long sessionPosition && sessionPosition > 0)
            {
                return sessionPosition;
            }

            if (session != null
                && _plugin.PartyParticipants.TryGetValue(party.Id, out var participants)
                && participants.TryGetValue(session.UserId, out var participant)
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
                    var isMaster = !string.IsNullOrEmpty(party.MasterUserId) && e.Session.UserId == party.MasterUserId;
                    var stoppedPosition = GetLastKnownPosition(party, e.Session);
                    var stoppedEpisodeId = FindSeriesEpisodeId(party, e.Item);
                    var currentEpisode = SeriesPartyQueue.GetCurrentEpisode(party);
                    var sourceItem = currentEpisode == null ? null : _libraryManager.GetItemById(currentEpisode.ItemId);
                    var runtimeTicks = e.Item?.RunTimeTicks ?? sourceItem?.RunTimeTicks ?? 0;

                    if (party.IsSeriesParty)
                    {
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

                    RemoveParticipant(party.Id, e.Session.UserId);
                    
                    if (isMaster)
                    {
                        _logger.Info($"[Watch Party] Master user {e.Session.UserId} stopped for party {party.Id} (keeping position)");
                        party.IsPlaying = false;
                        _playbackSyncCoordinator.StopMasterClock(
                            party.Id,
                            stoppedPosition,
                            DateTime.UtcNow);
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
                        
                        if (_plugin.PartyParticipants.ContainsKey(party.Id) && _plugin.PartyParticipants[party.Id].Count > 0)
                        {
                            var newHostUserId = _plugin.PartyParticipants[party.Id].Keys.First();
                            var newHostParticipant = _plugin.PartyParticipants[party.Id][newHostUserId];
                            party.HostUserId = newHostUserId;
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
                        if (_plugin.PartyReadyUsers.ContainsKey(party.Id))
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
    }
}
