using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Session;

namespace WatchPartyForEmby
{
    public class ServerEntryPoint : IServerEntryPoint
    {
        private readonly ISessionManager _sessionManager;
        private readonly ILibraryManager _libraryManager;
        private readonly ILibraryMonitor _libraryMonitor;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger _logger;
        private readonly Plugin _plugin;
        private Timer _syncTimer;
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _partySyncedSessions = new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>();
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, bool>> _partySessionPauseState = new ConcurrentDictionary<string, ConcurrentDictionary<string, bool>>();
        private readonly Dictionary<string, string> _partyHostSessions = new Dictionary<string, string>();
        private readonly HashSet<string> _trackedPartyIds = new HashSet<string>();
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> _partyPauseVotes = new ConcurrentDictionary<string, ConcurrentDictionary<string, int>>();
        private readonly Dictionary<string, string> _partyLibraryPathCache = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _partyStrmPathCache = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _partyEpisodeStrmPathCache = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _partySeriesDirectoryCache = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _strmContentCache = new Dictionary<string, string>();
        private readonly HashSet<string> _partiesTransitioning = new HashSet<string>();
        private readonly ConcurrentDictionary<string, long> _seriesSelectionVersions = new ConcurrentDictionary<string, long>();
        private readonly ConcurrentDictionary<string, DateTime> _expectedSeriesEpisodeStarts = new ConcurrentDictionary<string, DateTime>();
        private readonly object _seriesTransitionLock = new object();
        private readonly ConcurrentDictionary<string, DateTime> _lastProgressCheckpoint = new ConcurrentDictionary<string, DateTime>();
        private readonly PlaybackSyncCoordinator _playbackSyncCoordinator = new PlaybackSyncCoordinator();
        private static readonly TimeSpan ProgressCheckpointInterval = TimeSpan.FromSeconds(30);

        public ServerEntryPoint(
            ISessionManager sessionManager,
            ILibraryManager libraryManager,
            ILibraryMonitor libraryMonitor,
            IFileSystem fileSystem,
            ILogManager logManager)
        {
            _sessionManager = sessionManager;
            _libraryManager = libraryManager;
            _libraryMonitor = libraryMonitor;
            _fileSystem = fileSystem;
            _logger = logManager.GetLogger(GetType().Name);
            _plugin = Plugin.Instance;
        }

        private string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            try
            {
                var fullPath = Path.GetFullPath(path);
                
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    fullPath = fullPath.ToLowerInvariant();
                }
                
                return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path.Replace('\\', Path.DirectorySeparatorChar)
                          .Replace('/', Path.DirectorySeparatorChar)
                          .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }

        private StringComparison GetPathComparison()
        {
            return Environment.OSVersion.Platform == PlatformID.Win32NT
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
        }

        private StringComparer GetPathComparer()
        {
            return Environment.OSVersion.Platform == PlatformID.Win32NT
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
        }

        public void Run()
        {
            _logger.Info("Watch Party plugin started");
            
            _sessionManager.PlaybackStart += OnPlaybackStart;
            _sessionManager.PlaybackProgress += OnPlaybackProgress;
            _sessionManager.PlaybackStopped += OnPlaybackStopped;
            
            _plugin.ConfigurationUpdated += OnConfigurationUpdated;
            
            MigrateLegacyConfiguration();
            RepairSeriesPartyConfiguration();
            
            var config = _plugin.Configuration;
            foreach (var party in config.WatchParties)
            {
                _trackedPartyIds.Add(party.Id);
            }
            
            var intervalMs = Math.Max(1, config.SyncIntervalSeconds) * 1000;
            _syncTimer = new Timer(CheckAndSyncUsers, null, intervalMs, intervalMs);
            
            Task.Run(() => CreateAllWatchPartyStrmFiles());
        }

        private void MigrateLegacyConfiguration()
        {
            var config = _plugin.Configuration;
            
            if (!string.IsNullOrEmpty(config.SelectedItemId) && config.WatchParties.Count == 0)
            {
                _logger.Info("Migrating legacy single-party configuration");
                
                var legacyParty = new WatchPartyItem
                {
                    LibraryId = config.SelectedLibraryId,
                    ItemId = config.SelectedItemId,
                    ItemName = config.SelectedItemName,
                    ItemType = config.SelectedItemType,
                    SeriesId = config.SelectedSeriesId,
                    SeasonId = config.SelectedSeasonId,
                    CollectionName = config.CollectionName ?? "Watch Party",
                    IsActive = config.IsPartyActive,
                    CurrentPositionTicks = config.CurrentPositionTicks,
                    IsPlaying = config.IsPlaying,
                    MaxParticipants = config.MaxParticipants
                };
                
                config.WatchParties.Add(legacyParty);
                _plugin.SaveConfiguration();
                
                _logger.Info($"Migrated legacy party: {legacyParty.ItemName}");
            }
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
            Task.Run(() => ValidateAndCleanWatchParties());
            Task.Run(() => CreateAllWatchPartyStrmFiles());
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

        private void ValidateAndCleanWatchParties()
        {
            try
            {
                var config = _plugin.Configuration;
                bool configChanged = false;

                if (string.IsNullOrEmpty(config.WatchPartyStrmPath))
                {
                    return;
                }

                foreach (var party in config.WatchParties.ToList())
                {
                    var partyItem = _libraryManager.GetItemById(party.ItemId);
                    
                    if (partyItem == null)
                    {
                        _logger.Info($"Party {party.Id}: Item {party.ItemName} no longer exists in library, removing from settings");
                        config.WatchParties.Remove(party);
                        configChanged = true;
                        
                        var strmPath = GetStrmFilePath(party);
                        if (File.Exists(strmPath))
                        {
                            File.Delete(strmPath);
                            _logger.Info($"Deleted STRM file: {strmPath}");
                            
                            // Notify Emby about the file system change
                            _libraryMonitor.ReportFileSystemChanged(strmPath);
                            _logger.Info($"Notified Emby about STRM file deletion: {strmPath}");
                        }
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
                
                var removedStrmPaths = new HashSet<string>(GetPathComparer());
                var removedSeriesDirectories = new HashSet<string>(GetPathComparer());
                foreach (var removedId in removedPartyIds)
                {
                    _logger.Info($"Party {removedId} was removed, cleaning up...");
                    _playbackSyncCoordinator.ClearParty(removedId);
                    
                    if (_partyStrmPathCache.TryGetValue(removedId, out var removedStrmPath)
                        && !string.IsNullOrEmpty(removedStrmPath))
                    {
                        removedStrmPaths.Add(removedStrmPath);
                    }
                    if (_partySeriesDirectoryCache.TryGetValue(removedId, out var removedSeriesDirectory)
                        && !string.IsNullOrEmpty(removedSeriesDirectory))
                    {
                        removedSeriesDirectories.Add(removedSeriesDirectory);
                    }
                    
                    _partySyncedSessions.TryRemove(removedId, out _);
                    _partySessionPauseState.TryRemove(removedId, out _);
                    _partyHostSessions.Remove(removedId);
                    _partyPauseVotes.TryRemove(removedId, out _);
                    _partySeriesDirectoryCache.Remove(removedId);
                    _lastProgressCheckpoint.TryRemove(removedId, out _);
                    _seriesSelectionVersions.TryRemove(removedId, out _);

                    var episodeCacheKeys = _partyEpisodeStrmPathCache.Keys
                        .Where(key => key.StartsWith(removedId + ":", StringComparison.Ordinal))
                        .ToList();
                    foreach (var cacheKey in episodeCacheKeys)
                    {
                        _partyEpisodeStrmPathCache.Remove(cacheKey);
                    }
                }
                
                var removedCacheIds = _partyLibraryPathCache.Keys.Except(currentPartyIds).ToList();
                foreach (var removedId in removedCacheIds)
                {
                    _partyLibraryPathCache.Remove(removedId);
                    _partyStrmPathCache.Remove(removedId);
                }

                foreach (var strmFile in removedStrmPaths)
                {
                    if (!File.Exists(strmFile))
                    {
                        continue;
                    }

                    try
                    {
                        File.Delete(strmFile);
                        _logger.Info($"Deleted Watch Party STRM file: {strmFile}");
                        _libraryMonitor.ReportFileSystemChanged(strmFile);
                    }
                    catch (Exception ex)
                    {
                        _logger.ErrorException($"Error deleting Watch Party STRM file: {strmFile}", ex);
                    }
                }

                foreach (var seriesDirectory in removedSeriesDirectories)
                {
                    DeleteSeriesPartyDirectory(seriesDirectory);
                }
                
                _trackedPartyIds.Clear();
                _trackedPartyIds.UnionWith(currentPartyIds);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error cleaning up removed parties", ex);
            }
        }

        private async Task CreateAllWatchPartyStrmFiles()
        {
            var config = _plugin.Configuration;
            var createdPaths = new List<string>();

            foreach (var party in config.WatchParties)
            {
                createdPaths.AddRange(await CreateWatchPartyStrmFiles(party));
            }

            // Notify Emby about each new STRM file via the REST API (same approach as Radarr/Sonarr)
            if (createdPaths.Count > 0)
            {
                await Task.Delay(1000);
                await NotifyEmbyLibraryUpdated(createdPaths);
            }

            _logger.Info("Finished creating STRM files for all watch parties");
        }

        private async Task<List<string>> CreateWatchPartyStrmFiles(WatchPartyItem party)
        {
            if (!party.IsSeriesParty)
            {
                var path = await CreateWatchPartyStrmFile(party);
                return string.IsNullOrEmpty(path) ? new List<string>() : new List<string> { path };
            }

            var createdPaths = new List<string>();
            var seriesDirectory = GetSeriesPartyDirectory(party);
            if (string.IsNullOrEmpty(seriesDirectory))
            {
                return createdPaths;
            }

            Directory.CreateDirectory(seriesDirectory);
            var expectedPaths = new HashSet<string>(GetPathComparer());
            foreach (var episode in party.EpisodeQueue ?? new List<WatchPartyEpisode>())
            {
                var item = _libraryManager.GetItemById(episode.ItemId);
                if (item == null || string.IsNullOrEmpty(item.Path))
                {
                    _logger.Warn($"Party {party.Id}: Queue item {episode.ItemId} was not found or has no path");
                    continue;
                }

                var path = GetSeriesEpisodeStrmPath(party, episode);
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var resolvedSource = await StrmSourceResolver.ResolveAsync(item.Path);
                await File.WriteAllTextAsync(path, resolvedSource + Environment.NewLine);
                _strmContentCache[path] = resolvedSource;
                expectedPaths.Add(NormalizePath(path));
                createdPaths.Add(path);
            }

            foreach (var existingPath in Directory.EnumerateFiles(seriesDirectory, "*.strm", SearchOption.AllDirectories))
            {
                if (!expectedPaths.Contains(NormalizePath(existingPath)))
                {
                    File.Delete(existingPath);
                    _strmContentCache.Remove(existingPath);
                    _libraryMonitor.ReportFileSystemChanged(existingPath);
                    _logger.Info($"Deleted stale Series Party STRM file: {existingPath}");
                }
            }

            _logger.Info($"Created {createdPaths.Count} STRM files for Series Party {party.Id}");
            return createdPaths;
        }

        private async Task NotifyEmbyLibraryUpdated(List<string> paths)
        {
            var apiKey = _plugin.Configuration.EmbyApiKey;
            if (string.IsNullOrEmpty(apiKey))
            {
                _logger.Warn("No Emby API key configured, cannot notify library about new STRM files");
                return;
            }

            try
            {
                // Refresh the Watch Party library folder to pick up new/removed STRM files
                var targetLibraryId = _plugin.Configuration.StrmTargetLibraryId;
                if (!string.IsNullOrEmpty(targetLibraryId))
                {
                    using (var client = new System.Net.Http.HttpClient())
                    {
                        client.Timeout = TimeSpan.FromSeconds(30);
                        var request = new System.Net.Http.HttpRequestMessage(
                            System.Net.Http.HttpMethod.Post,
                            EmbyServerAddress.Build(
                                _plugin.Configuration.EmbyServerUrl,
                                $"emby/Items/{targetLibraryId}/Refresh?Recursive=true"));
                        request.Headers.Add("X-Emby-Token", apiKey);

                        var response = await client.SendAsync(request);
                        _logger.Info($"Refreshed Watch Party library (ID: {targetLibraryId}) - Status: {response.StatusCode}");
                    }
                }
                else
                {
                    _logger.Warn("No StrmTargetLibraryId configured, falling back to LibraryMonitor");
                    foreach (var path in paths)
                    {
                        _libraryMonitor.ReportFileSystemChanged(path);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error refreshing Watch Party library", ex);
                foreach (var path in paths)
                {
                    _libraryMonitor.ReportFileSystemChanged(path);
                }
            }
        }

        private string GetLibraryPath(WatchPartyItem party)
        {
            if (_partyLibraryPathCache.TryGetValue(party.Id, out var cachedPath))
            {
                return cachedPath;
            }

            _logger.Debug($"GetLibraryPath: Starting lookup for party {party.Id}");
            _logger.Debug($"  TargetLibraryPath: '{party.TargetLibraryPath}'");
            _logger.Debug($"  TargetLibraryId: '{party.TargetLibraryId}'");
            _logger.Debug($"  CollectionName: '{party.CollectionName}'");

            if (!string.IsNullOrEmpty(party.TargetLibraryPath) && Directory.Exists(party.TargetLibraryPath))
            {
                _logger.Debug($"GetLibraryPath: Using TargetLibraryPath: {party.TargetLibraryPath}");
                _partyLibraryPathCache[party.Id] = party.TargetLibraryPath;
                return party.TargetLibraryPath;
            }

            try
            {
                var virtualFolders = _libraryManager.GetVirtualFolders();
                _logger.Debug($"GetLibraryPath: Found {virtualFolders.Count} virtual folders");

                if (!string.IsNullOrEmpty(party.TargetLibraryId))
                {
                    _logger.Debug($"GetLibraryPath: Looking for library with ID: {party.TargetLibraryId}");
                    
                    foreach (var vf in virtualFolders)
                    {
                        _logger.Debug($"  Checking virtual folder: Name='{vf.Name}', ItemId='{vf.ItemId}', Locations={vf.Locations?.Length ?? 0}");
                        
                        if (vf.ItemId == party.TargetLibraryId && vf.Locations != null && vf.Locations.Length > 0)
                        {
                            var path = vf.Locations[0];
                            _logger.Debug($"GetLibraryPath: Found library path by ID: {path}");
                            _partyLibraryPathCache[party.Id] = path;
                            return path;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(party.CollectionName))
                {
                    _logger.Debug($"GetLibraryPath: Looking for library with name: {party.CollectionName}");
                    
                    foreach (var vf in virtualFolders)
                    {
                        if (vf.Name.Equals(party.CollectionName, StringComparison.OrdinalIgnoreCase) 
                            && vf.Locations != null && vf.Locations.Length > 0)
                        {
                            var path = vf.Locations[0];
                            _logger.Debug($"GetLibraryPath: Found library path by name: {path}");
                            _partyLibraryPathCache[party.Id] = path;
                            return path;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("GetLibraryPath: Error getting virtual folders", ex);
            }

            _logger.Warn($"GetLibraryPath: Could not find library path for party {party.Id}");
            return null;
        }

        private string GetSeriesPartyDirectory(WatchPartyItem party)
        {
            if (_partySeriesDirectoryCache.TryGetValue(party.Id, out var cachedDirectory))
            {
                return cachedDirectory;
            }

            var libraryPath = GetLibraryPath(party);
            if (string.IsNullOrEmpty(libraryPath))
            {
                return null;
            }

            var seriesName = SanitizeFileName(party.SeriesName ?? party.ItemName ?? "Series");
            var compactId = (party.Id ?? Guid.NewGuid().ToString("N")).Replace("-", string.Empty);
            var directory = Path.Combine(libraryPath, $"{seriesName} [Series Party {compactId}]");
            _partySeriesDirectoryCache[party.Id] = directory;
            return directory;
        }

        private string GetSeriesEpisodeStrmPath(WatchPartyItem party, WatchPartyEpisode episode)
        {
            if (episode == null || string.IsNullOrEmpty(episode.ItemId))
            {
                return null;
            }

            var cacheKey = $"{party.Id}:{episode.ItemId}";
            if (_partyEpisodeStrmPathCache.TryGetValue(cacheKey, out var cachedPath))
            {
                return cachedPath;
            }

            var seriesDirectory = GetSeriesPartyDirectory(party);
            if (string.IsNullOrEmpty(seriesDirectory))
            {
                return null;
            }

            var seasonDirectory = Path.Combine(seriesDirectory, $"Season {episode.SeasonNumber:00}");
            var seriesName = SanitizeFileName(party.SeriesName ?? "Series");
            var episodeName = SanitizeFileName(episode.ItemName ?? $"Episode {episode.EpisodeNumber}");
            var episodeId = SanitizeFileName(episode.ItemId);
            var fileName = $"{seriesName} - S{episode.SeasonNumber:00}E{episode.EpisodeNumber:00} - {episodeName} [{episodeId}].strm";
            var path = Path.Combine(seasonDirectory, fileName);
            _partyEpisodeStrmPathCache[cacheKey] = path;
            return path;
        }

        private string SanitizeFileName(string value)
        {
            var sanitized = string.Join("_", (value ?? string.Empty).Split(Path.GetInvalidFileNameChars()));
            return string.IsNullOrWhiteSpace(sanitized) ? "Watch Party" : sanitized.Trim();
        }

        private void DeleteSeriesPartyDirectory(string seriesDirectory)
        {
            try
            {
                if (!Directory.Exists(seriesDirectory))
                {
                    return;
                }

                foreach (var strmFile in Directory.EnumerateFiles(seriesDirectory, "*.strm", SearchOption.AllDirectories))
                {
                    File.Delete(strmFile);
                    _strmContentCache.Remove(strmFile);
                    _libraryMonitor.ReportFileSystemChanged(strmFile);
                }

                foreach (var directory in Directory.EnumerateDirectories(seriesDirectory, "*", SearchOption.AllDirectories)
                    .OrderByDescending(path => path.Length))
                {
                    if (!Directory.EnumerateFileSystemEntries(directory).Any())
                    {
                        Directory.Delete(directory);
                    }
                }

                if (!Directory.EnumerateFileSystemEntries(seriesDirectory).Any())
                {
                    Directory.Delete(seriesDirectory);
                }

                _logger.Info($"Deleted Series Party directory: {seriesDirectory}");
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"Error deleting Series Party directory {seriesDirectory}", ex);
            }
        }

        private async Task<string> CreateWatchPartyStrmFile(WatchPartyItem party)
        {
            try
            {
                var libraryPath = GetLibraryPath(party);
                if (string.IsNullOrEmpty(libraryPath))
                {
                    _logger.Warn($"Party {party.Id}: Could not determine library path, skipping STRM file creation");
                    return null;
                }

                if (string.IsNullOrEmpty(party.ItemId))
                {
                    _logger.Info($"Party {party.Id}: No content selected, skipping STRM file creation");
                    return null;
                }

                var item = _libraryManager.GetItemById(party.ItemId);
                if (item == null)
                {
                    _logger.Warn($"Party {party.Id}: Selected item ID {party.ItemId} not found");
                    return null;
                }

                var strmPath = GetStrmFilePath(party);
                var itemPath = item.Path;

                if (string.IsNullOrEmpty(itemPath))
                {
                    _logger.Warn($"Party {party.Id}: Item {item.Name} has no path");
                    return null;
                }

                var resolvedSource = await StrmSourceResolver.ResolveAsync(itemPath);
                await File.WriteAllTextAsync(strmPath, resolvedSource + Environment.NewLine);

                if (!string.Equals(itemPath, resolvedSource, StringComparison.Ordinal))
                {
                    _logger.Info($"Party {party.Id}: Resolved source STRM before creating watch party entry");
                }

                _logger.Info($"Created STRM file for party {party.Id}: {strmPath}");

                return strmPath;
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"Error creating STRM file for party {party.Id}", ex);
                return null;
            }
        }

        private string GetStrmFilePath(WatchPartyItem party)
        {
            if (party.IsSeriesParty)
            {
                return GetSeriesEpisodeStrmPath(party, SeriesPartyQueue.GetCurrentEpisode(party));
            }

            if (_partyStrmPathCache.TryGetValue(party.Id, out var cachedStrmPath))
            {
                return cachedStrmPath;
            }

            var libraryPath = GetLibraryPath(party);
            if (string.IsNullOrEmpty(libraryPath))
            {
                return null;
            }

            var item = _libraryManager.GetItemById(party.ItemId);
            
            string strmPath;
            if (item != null && !string.IsNullOrEmpty(item.Path))
            {
                var originalFileName = Path.GetFileNameWithoutExtension(item.Path);
                strmPath = Path.Combine(libraryPath, $"{originalFileName}.strm");
            }
            else
            {
                var sanitizedName = string.Join("_", party.ItemName.Split(Path.GetInvalidFileNameChars()));
                strmPath = Path.Combine(libraryPath, $"{sanitizedName}.strm");
            }

            _partyStrmPathCache[party.Id] = strmPath;
            return strmPath;
        }

        private async void OnPlaybackStart(object sender, PlaybackProgressEventArgs e)
        {
            try
            {
                _logger.Info($"[Watch Party] PlaybackStart event fired - Item: {e.Item?.Name}, ItemId: {e.Item?.Id}, UserId: {e.Session?.UserId}");
                
                var party = FindPartyForItem(e.Item, includeQueuedSeriesEpisodes: true);
                
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
                            || !string.Equals(startedEpisodeId, currentEpisode.ItemId, StringComparison.OrdinalIgnoreCase)))
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
                                currentEpisode,
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
                
                if (config.WatchParties.Count == 0
                    && !string.IsNullOrEmpty(config.SelectedItemId)
                    && config.IsPartyActive
                    && config.IsPlaying
                    && config.CurrentPositionTicks > 0)
                {
                    var sessions = _sessionManager.Sessions.Where(s => s.NowPlayingItem != null).ToList();
                    
                    foreach (var session in sessions)
                    {
                        var item = _libraryManager.GetItemById(session.NowPlayingItem.Id);
                        if (item != null && IsSelectedItem(item, config))
                        {
                            var currentPosition = session.PlayState?.PositionTicks ?? 0;
                            var positionDifference = Math.Abs(currentPosition - config.CurrentPositionTicks);
                            var syncThreshold = TimeSpan.FromSeconds(10).Ticks;
                            
                            if (positionDifference > syncThreshold)
                            {
                                _logger.Info($"[Watch Party] Session {session.Id} is {TimeSpan.FromTicks(positionDifference).TotalSeconds:F1}s behind, syncing");
                                Task.Run(async () => await SyncUserToPosition(session, item, config.CurrentPositionTicks));
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

        private WatchPartyItem FindPartyForItem(BaseItem item, bool includeQueuedSeriesEpisodes = false)
        {
            if (item == null) return null;
            
            var config = _plugin.Configuration;

            if (includeQueuedSeriesEpisodes)
            {
                foreach (var party in config.WatchParties.Where(candidate => candidate.IsSeriesParty && candidate.IsActive))
                {
                    if (!string.IsNullOrEmpty(FindSeriesEpisodeId(party, item, allowSourceItemMatch: false)))
                    {
                        return party;
                    }
                }
            }
            
            // First, check if this is a STRM file
            var isStrmFile = !string.IsNullOrEmpty(item.Path) && item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase);
            
            if (isStrmFile)
            {
                var itemPath = NormalizePath(item.Path);
                _logger.Debug($"[Watch Party] Checking if STRM file {itemPath} belongs to a watch party");
                
                // Method 1: Check by STRM file path
                foreach (var party in config.WatchParties)
                {
                    if (string.IsNullOrEmpty(party.ItemId)) continue;
                    
                    var partyStrmPath = GetStrmFilePath(party);
                    if (!string.IsNullOrEmpty(partyStrmPath))
                    {
                        var normalizedPartyPath = NormalizePath(partyStrmPath);
                        if (string.Equals(itemPath, normalizedPartyPath, GetPathComparison()))
                        {
                            _logger.Debug($"[Watch Party] STRM file path {itemPath} matches party {party.Id}");
                            return party;
                        }
                    }
                }
                
                // Method 2: Check by STRM content (reads the file and matches the target path)
                try
                {
                    if (File.Exists(item.Path))
                    {
                        if (!_strmContentCache.TryGetValue(item.Path, out var strmContent))
                        {
                            strmContent = File.ReadAllText(item.Path).Trim();
                            _strmContentCache[item.Path] = strmContent;
                        }
                        
                        _logger.Debug($"[Watch Party] STRM file content: {strmContent}");
                        
                        foreach (var party in config.WatchParties)
                        {
                            if (string.IsNullOrEmpty(party.ItemId)) continue;
                            
                            var partyItem = _libraryManager.GetItemById(party.ItemId);
                            if (partyItem != null && !string.IsNullOrEmpty(partyItem.Path))
                            {
                                var normalizedStrmContent = NormalizePath(strmContent);
                                var normalizedPartyItemPath = NormalizePath(partyItem.Path);
                                if (string.Equals(normalizedStrmContent, normalizedPartyItemPath, GetPathComparison()))
                                {
                                    _logger.Debug($"[Watch Party] STRM file content matches party {party.Id} item path");
                                    return party;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.ErrorException($"Error reading STRM file {itemPath}", ex);
                }
            }
            
            // Check by item ID directly
            foreach (var party in config.WatchParties)
            {
                if (string.IsNullOrEmpty(party.ItemId)) continue;
                
                if (Guid.TryParse(party.ItemId, out var partyItemGuid) && item.Id == partyItemGuid)
                {
                    return party;
                }
                else if (long.TryParse(party.ItemId, out var partyInternalId) && item.InternalId == partyInternalId)
                {
                    return party;
                }
            }
            
            if (!string.IsNullOrEmpty(config.SelectedItemId))
            {
                if (Guid.TryParse(config.SelectedItemId, out var selectedItemGuid) && item.Id == selectedItemGuid)
                {
                    return new WatchPartyItem
                    {
                        Id = "legacy",
                        ItemId = config.SelectedItemId,
                        ItemName = config.SelectedItemName,
                        IsActive = config.IsPartyActive,
                        CurrentPositionTicks = config.CurrentPositionTicks,
                        IsPlaying = config.IsPlaying,
                        MaxParticipants = config.MaxParticipants
                    };
                }
                else if (long.TryParse(config.SelectedItemId, out var selectedInternalId) && item.InternalId == selectedInternalId)
                {
                    return new WatchPartyItem
                    {
                        Id = "legacy",
                        ItemId = config.SelectedItemId,
                        ItemName = config.SelectedItemName,
                        IsActive = config.IsPartyActive,
                        CurrentPositionTicks = config.CurrentPositionTicks,
                        IsPlaying = config.IsPlaying,
                        MaxParticipants = config.MaxParticipants
                    };
                }
            }
            
            return null;
        }

        private string FindSeriesEpisodeId(
            WatchPartyItem party,
            BaseItem item,
            bool allowSourceItemMatch = true)
        {
            if (party?.IsSeriesParty != true || item == null)
            {
                return null;
            }

            var itemPath = NormalizePath(item.Path);
            foreach (var episode in party.EpisodeQueue ?? new List<WatchPartyEpisode>())
            {
                if (string.IsNullOrEmpty(episode?.ItemId))
                {
                    continue;
                }

                var generatedPath = NormalizePath(GetSeriesEpisodeStrmPath(party, episode));
                if (!string.IsNullOrEmpty(itemPath)
                    && string.Equals(itemPath, generatedPath, GetPathComparison()))
                {
                    return episode.ItemId;
                }

                if (!allowSourceItemMatch)
                {
                    continue;
                }

                var sourceItem = _libraryManager.GetItemById(episode.ItemId);
                if (sourceItem != null && (sourceItem.Id == item.Id || sourceItem.InternalId == item.InternalId))
                {
                    return episode.ItemId;
                }
            }

            return null;
        }

        private bool IsSelectedItem(BaseItem item, PluginConfiguration config)
        {
            if (item == null || string.IsNullOrEmpty(config.SelectedItemId))
            {
                return false;
            }
            
            if (Guid.TryParse(config.SelectedItemId, out var selectedItemGuid))
            {
                return item.Id == selectedItemGuid;
            }
            else if (long.TryParse(config.SelectedItemId, out var selectedInternalId))
            {
                return item.InternalId == selectedInternalId;
            }
            
            return false;
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
            var generatedPath = episode == null ? null : GetSeriesEpisodeStrmPath(party, episode);
            var episodeItem = string.IsNullOrEmpty(generatedPath)
                ? null
                : _libraryManager.FindByPath(generatedPath, false);
            episodeItem = episodeItem ?? (episode == null ? null : _libraryManager.GetItemById(episode.ItemId));
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

            if (!string.IsNullOrEmpty(generatedPath)
                && !string.Equals(NormalizePath(episodeItem.Path), NormalizePath(generatedPath), GetPathComparison()))
            {
                _logger.Warn(
                    $"[Party {party.Id}] Generated next-episode item is not indexed yet; falling back to the source library item");
            }

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
