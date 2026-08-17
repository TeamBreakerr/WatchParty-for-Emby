using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Model.Plugins;

namespace WatchPartyForEmby
{
    public static class PasswordHelper
    {
        private const int SaltSize = 16;
        private const int HashSize = 32;
        private const int Iterations = 600000;
        private const int LegacyIterations = 10000;

        public static string HashPassword(string password)
        {
            if (string.IsNullOrEmpty(password)) return string.Empty;
            
            var salt = new byte[SaltSize];
            RandomNumberGenerator.Fill(salt);
            
            using (var pbkdf2 = new Rfc2898DeriveBytes(password, salt, Iterations))
            {
                var hash = pbkdf2.GetBytes(HashSize);
                var hashBytes = new byte[SaltSize + HashSize];
                Array.Copy(salt, 0, hashBytes, 0, SaltSize);
                Array.Copy(hash, 0, hashBytes, SaltSize, HashSize);
                return Convert.ToBase64String(hashBytes);
            }
        }
        
        public static bool VerifyPassword(string password, string hash)
        {
            if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(hash)) return false;
            
            try
            {
                var hashBytes = Convert.FromBase64String(hash);
                
                // Check if it's a new PBKDF2 hash (salt + hash = 48 bytes)
                if (hashBytes.Length == SaltSize + HashSize)
                {
                    var salt = new byte[SaltSize];
                    Array.Copy(hashBytes, 0, salt, 0, SaltSize);
                    
                    foreach (var iterations in new[] { Iterations, LegacyIterations })
                    {
                        using (var pbkdf2 = new Rfc2898DeriveBytes(password, salt, iterations))
                        {
                            var testHash = pbkdf2.GetBytes(HashSize);
                            var storedHash = new byte[HashSize];
                            Array.Copy(hashBytes, SaltSize, storedHash, 0, HashSize);
                            if (CryptographicOperations.FixedTimeEquals(storedHash, testHash))
                            {
                                return true;
                            }
                        }
                    }
                    return false;
                }
                // Fall back to old SHA-256 verification for backward compatibility
                else if (hashBytes.Length == 32)
                {
                    using (var sha256 = SHA256.Create())
                    {
                        var bytes = Encoding.UTF8.GetBytes(password);
                        var testHash = sha256.ComputeHash(bytes);
                        return CryptographicOperations.FixedTimeEquals(hashBytes, testHash);
                    }
                }
                
                return false;
            }
            catch
            {
                return false;
            }
        }
    }

    public class SessionToken
    {
        public string Token { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public string IpAddress { get; set; }
        
        public bool IsValid()
        {
            return DateTime.UtcNow < ExpiresAt;
        }
    }

    public class RateLimitEntry
    {
        public int RequestCount { get; set; }
        public DateTime WindowStart { get; set; }
        public DateTime? BlockedUntil { get; set; }
    }

    public class PartyParticipant
    {
        public string UserId { get; set; }
        public string UserName { get; set; }
        public string SessionId { get; set; }
        public string PlaySessionId { get; set; }
        public DateTime JoinedAt { get; set; }
        public DateTime LastActivityAt { get; set; }
        public long CurrentPositionTicks { get; set; }
        public bool IsPaused { get; set; }
        public bool IsBuffering { get; set; }
        
        public PartyParticipant()
        {
            JoinedAt = DateTime.UtcNow;
            LastActivityAt = DateTime.UtcNow;
        }
    }

    public class WatchPartyEpisode
    {
        public string ItemId { get; set; }
        public string ItemName { get; set; }
        public string SeasonId { get; set; }
        public int SeasonNumber { get; set; }
        public int EpisodeNumber { get; set; }
    }

    public class WatchPartyItem
    {
        public string Id { get; set; }
        public string LibraryId { get; set; }
        public string ItemId { get; set; }
        public string ItemName { get; set; }
        public string ItemType { get; set; }
        public string SeriesId { get; set; }
        public string SeriesName { get; set; }
        public string SeasonId { get; set; }
        public bool IsSeriesParty { get; set; }
        public List<WatchPartyEpisode> EpisodeQueue { get; set; }
        public int CurrentEpisodeIndex { get; set; }
        public string CurrentEpisodeId { get; set; }
        public bool IsActive { get; set; }
        public long CurrentPositionTicks { get; set; }
        public bool IsPlaying { get; set; }
        public int MaxParticipants { get; set; }
        public DateTime CreatedDate { get; set; }
        
        public List<string> AllowedUserIds { get; set; }
        public string HostUserId { get; set; }
        public string MasterUserId { get; set; }
        public string PasswordHash { get; set; }
        public bool IsWaitingRoom { get; set; }
        public bool AutoStartWhenReady { get; set; }
        public int MinReadyCount { get; set; }
        public string PauseControl { get; set; }
        public int SyncToleranceSeconds { get; set; }
        public int MaxBufferThresholdSeconds { get; set; }
        public bool AutoKickInactiveMinutes { get; set; }
        public int InactiveTimeoutMinutes { get; set; }

        public WatchPartyItem()
        {
            Id = Guid.NewGuid().ToString();
            IsActive = true;
            MaxParticipants = 50;
            CreatedDate = DateTime.UtcNow;
            AllowedUserIds = new List<string>();
            EpisodeQueue = new List<WatchPartyEpisode>();
            CurrentEpisodeIndex = -1;
            IsWaitingRoom = true;
            AutoStartWhenReady = true;
            MinReadyCount = 1;
            PauseControl = "Anyone";
            SyncToleranceSeconds = 2;
            MaxBufferThresholdSeconds = 30;
            AutoKickInactiveMinutes = true;
            InactiveTimeoutMinutes = 15;
        }
    }

    public class PluginConfiguration : BasePluginConfiguration
    {
        public int ConfigurationVersion { get; set; }
        public List<WatchPartyItem> WatchParties { get; set; }

        public int SyncIntervalSeconds { get; set; } = 5;
        public int SyncOffsetMilliseconds { get; set; } = 1000;

        public bool EnableExternalWebServer { get; set; } = false;
        public int ExternalWebServerPort { get; set; } = 8097;
        public string ListenAddress { get; set; } = "127.0.0.1";
        public string AllowedCorsOrigins { get; set; } = "";
        public string AdminPasswordHash { get; set; }
        public string EmbyApiKey { get; set; }
        public string ExternalServerUrl { get; set; }
        public string EmbyServerUrl { get; set; }
        public int SessionExpirationMinutes { get; set; } = 60;
        public int RateLimitRequestsPerMinute { get; set; } = 60;
        public int RateLimitBlockDurationMinutes { get; set; } = 15;

        // Reverse Proxy Mode
        public bool UseReverseProxy { get; set; } = false;

        // HTTPS Settings (disabled when using reverse proxy)
        public bool EnableHttps { get; set; } = false;
        public string HttpsCertificateThumbprint { get; set; } = "";

        // CSRF Protection
        public bool EnableCsrfProtection { get; set; } = true;

        // Security Headers (disabled when using reverse proxy)
        public bool EnableSecurityHeaders { get; set; } = true;
        public string ContentSecurityPolicy { get; set; } = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:;";
        public bool EnableHsts { get; set; } = true;
        public int HstsMaxAge { get; set; } = 31536000;

        // Account Lockout
        public bool EnableAccountLockout { get; set; } = true;
        public int MaxFailedLoginAttempts { get; set; } = 5;
        public int LockoutDurationMinutes { get; set; } = 15;
        public int LockoutWindowMinutes { get; set; } = 10;

        // Audit Logging
        public bool EnableAuditLogging { get; set; } = true;
        public int MaxAuditLogEntries { get; set; } = 1000;

        public PluginConfiguration()
        {
            ConfigurationVersion = 0;
            WatchParties = new List<WatchPartyItem>();
            SyncIntervalSeconds = 5;
            SyncOffsetMilliseconds = 1000;
            ExternalWebServerPort = 8097;
            ListenAddress = "127.0.0.1";
            AllowedCorsOrigins = "";
            AdminPasswordHash = string.Empty;
            ExternalServerUrl = string.Empty;
            EmbyServerUrl = string.Empty;
            SessionExpirationMinutes = 60;
            RateLimitRequestsPerMinute = 60;
            RateLimitBlockDurationMinutes = 15;
            UseReverseProxy = false;
            EnableHttps = false;
            HttpsCertificateThumbprint = string.Empty;
            EnableCsrfProtection = true;
            EnableSecurityHeaders = true;
            ContentSecurityPolicy = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:;";
            EnableHsts = true;
            HstsMaxAge = 31536000;
            EnableAccountLockout = true;
            MaxFailedLoginAttempts = 5;
            LockoutDurationMinutes = 15;
            LockoutWindowMinutes = 10;
            EnableAuditLogging = true;
            MaxAuditLogEntries = 1000;
        }
    }
}
