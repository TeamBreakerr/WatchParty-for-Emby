using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WatchPartyForEmby
{
    public class ExternalWebServer
    {
        private const int MaxRequestBodyBytes = 1024 * 1024;
        private const int MaxConcurrentRequests = 8;

        private readonly ILogger _logger;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly HttpListener _listener;
        private CancellationTokenSource _cancellationTokenSource;
        private readonly int _port;
        private readonly string _listenAddress;
        private readonly Dictionary<string, SessionToken> _activeSessions = new Dictionary<string, SessionToken>();
        private readonly Dictionary<string, RateLimitEntry> _rateLimits = new Dictionary<string, RateLimitEntry>();
        private readonly CsrfTokenRegistry _csrfTokens = new CsrfTokenRegistry();
        private readonly Dictionary<string, AccountLockout> _loginAttempts = new Dictionary<string, AccountLockout>();
        private readonly List<AuditLogEntry> _auditLog = new List<AuditLogEntry>();
        private readonly object _sessionLock = new object();
        private readonly object _rateLimitLock = new object();
        private readonly object _loginLock = new object();
        private readonly object _auditLock = new object();
        private readonly SemaphoreSlim _requestConcurrency = new SemaphoreSlim(
            MaxConcurrentRequests,
            MaxConcurrentRequests);

        public ExternalWebServer(ILogger logger, IJsonSerializer jsonSerializer, int port, string listenAddress = "127.0.0.1")
        {
            _logger = logger;
            _jsonSerializer = jsonSerializer;
            _port = port;
            _listenAddress = listenAddress;
            _cancellationTokenSource = new CancellationTokenSource();

            _listener = new HttpListener();

            var config = Plugin.Instance.Configuration;
            var useHttps = config.EnableHttps && !config.UseReverseProxy;
            var protocol = useHttps ? "https" : "http";

            // Support comma-separated listen addresses
            var addresses = listenAddress.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                        .Select(addr => addr.Trim())
                                        .Where(addr => !string.IsNullOrEmpty(addr))
                                        .ToList();

            if (addresses.Count == 0)
            {
                addresses.Add("127.0.0.1");
            }

            foreach (var address in addresses)
            {
                if (address == "*" || address == "0.0.0.0")
                {
                    _listener.Prefixes.Add($"{protocol}://*:{port}/");
                }
                else if (address == "+")
                {
                    _listener.Prefixes.Add($"{protocol}://+:{port}/");
                }
                else
                {
                    _listener.Prefixes.Add($"{protocol}://{address}:{port}/");
                }
            }

            if (!useHttps && !config.UseReverseProxy)
            {
                _logger.Warn($"[ExternalWebServer] WARNING: Using HTTP without encryption. All data transmits in plain text.");
            }
            else if (config.UseReverseProxy)
            {
                _logger.Info($"[ExternalWebServer] Reverse proxy mode enabled. HTTPS, security headers, and CORS handled by proxy.");
            }

            if (addresses.Any(a => a == "*" || a == "0.0.0.0" || a == "+"))
            {
                _logger.Warn($"[ExternalWebServer] WARNING: Binding to all network interfaces. This may expose sensitive data.");
            }

            if (useHttps)
            {
                _logger.Info($"[ExternalWebServer] HTTPS enabled. Ensure certificate is bound: netsh http add sslcert ipport=0.0.0.0:{port} certhash={config.HttpsCertificateThumbprint} appid={{00000000-0000-0000-0000-000000000000}}");
            }
        }

        public string Start()
        {
            try
            {
                _listener.Start();
                var prefixes = string.Join(", ", _listener.Prefixes);
                _logger.Info($"[ExternalWebServer] Started and listening on: {prefixes}");
                Task.Run(() => Listen(_cancellationTokenSource.Token));
                Task.Run(() => CleanupExpiredSessions(_cancellationTokenSource.Token));
                return $"Running on {_listenAddress}:{_port}";
            }
            catch (HttpListenerException ex) when (ex.ErrorCode == 5)
            {
                var config = Plugin.Instance.Configuration;
                var useHttps = config.EnableHttps && !config.UseReverseProxy;
                var protocol = useHttps ? "https" : "http";

                var addresses = _listenAddress.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                            .Select(addr => addr.Trim())
                                            .Where(addr => !string.IsNullOrEmpty(addr));

                var commands = new List<string>();
                foreach (var address in addresses)
                {
                    var urlPattern = (address == "*" || address == "0.0.0.0") ? "*" :
                                    (address == "+") ? "+" : address;
                    commands.Add($"netsh http add urlacl url={protocol}://{urlPattern}:{_port}/ user=\"Everyone\"");
                }

                var errorMsg = $"Error: Access Denied. Run as Administrator:\n" + string.Join("\n", commands);
                _logger.Error(errorMsg);
                return errorMsg;
            }
            catch (Exception ex)
            {
                var errorMsg = "[ExternalWebServer] Failed to start";
                _logger.ErrorException(errorMsg, ex);
                return errorMsg;
            }
        }

        public void Stop()
        {
            _cancellationTokenSource.Cancel();
            if (_listener.IsListening)
            {
                _listener.Stop();
                _listener.Close();
            }
            lock (_sessionLock)
            {
                _activeSessions.Clear();
            }
            _csrfTokens.Clear();
            _logger.Info("[ExternalWebServer] Stopped.");
        }

        private async Task CleanupExpiredSessions(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(5), token);

                    lock (_sessionLock)
                    {
                        var expiredTokens = _activeSessions
                            .Where(kvp => !kvp.Value.IsValid())
                            .Select(kvp => kvp.Key)
                            .ToList();

                        foreach (var expiredToken in expiredTokens)
                        {
                            _activeSessions.Remove(expiredToken);
                        }

                        if (expiredTokens.Count > 0)
                        {
                            _logger.Debug($"[ExternalWebServer] Cleaned up {expiredTokens.Count} expired sessions");
                        }
                    }

                    _csrfTokens.RemoveExpired();
                }
                catch (TaskCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.ErrorException("[ExternalWebServer] Error cleaning up sessions", ex);
                }
            }
        }

        private bool CheckRateLimit(string ipAddress)
        {
            var config = Plugin.Instance.Configuration;
            if (config.RateLimitRequestsPerMinute <= 0) return true;

            lock (_rateLimitLock)
            {
                if (!_rateLimits.ContainsKey(ipAddress))
                {
                    _rateLimits[ipAddress] = new RateLimitEntry
                    {
                        RequestCount = 1,
                        WindowStart = DateTime.UtcNow
                    };
                    return true;
                }

                var entry = _rateLimits[ipAddress];

                if (entry.BlockedUntil.HasValue)
                {
                    if (DateTime.UtcNow < entry.BlockedUntil.Value)
                    {
                        return false;
                    }
                    entry.BlockedUntil = null;
                    entry.RequestCount = 0;
                    entry.WindowStart = DateTime.UtcNow;
                }

                if ((DateTime.UtcNow - entry.WindowStart).TotalMinutes >= 1)
                {
                    entry.RequestCount = 1;
                    entry.WindowStart = DateTime.UtcNow;
                    return true;
                }

                entry.RequestCount++;

                if (entry.RequestCount > config.RateLimitRequestsPerMinute)
                {
                    entry.BlockedUntil = DateTime.UtcNow.AddMinutes(config.RateLimitBlockDurationMinutes);
                    _logger.Warn($"[ExternalWebServer] Rate limit exceeded for {ipAddress}. Blocked for {config.RateLimitBlockDurationMinutes} minutes.");
                    return false;
                }

                return true;
            }
        }

        private string GenerateSessionToken()
        {
            var tokenBytes = new byte[32];
            RandomNumberGenerator.Fill(tokenBytes);
            return Convert.ToBase64String(tokenBytes);
        }

        private string CreateSession(string ipAddress)
        {
            var config = Plugin.Instance.Configuration;
            var token = GenerateSessionToken();

            lock (_sessionLock)
            {
                _activeSessions[token] = new SessionToken
                {
                    Token = token,
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddMinutes(config.SessionExpirationMinutes),
                    IpAddress = ipAddress
                };
            }

            _logger.Debug($"[ExternalWebServer] Created session for {ipAddress}");
            return token;
        }

        private bool ValidateSession(string token, string ipAddress)
        {
            if (string.IsNullOrEmpty(token)) return false;

            lock (_sessionLock)
            {
                if (!_activeSessions.TryGetValue(token, out var session))
                    return false;

                if (!session.IsValid())
                {
                    _activeSessions.Remove(token);
                    return false;
                }

                if (session.IpAddress != ipAddress)
                {
                    _logger.Warn($"[ExternalWebServer] Session token used from different IP. Expected: {session.IpAddress}, Got: {ipAddress}");
                    return false;
                }

                return true;
            }
        }

        private bool ValidateInput(string input, int maxLength = 500)
        {
            if (string.IsNullOrEmpty(input)) return true;
            if (input.Length > maxLength) return false;

            // Check for null bytes and control characters
            return !input.Any(c => c == '\0' || (char.IsControl(c) && c != '\r' && c != '\n' && c != '\t'));
        }

        private string GenerateCsrfToken(string ipAddress)
        {
            var config = Plugin.Instance.Configuration;
            if (!config.EnableCsrfProtection) return null;

            var tokenBytes = new byte[32];
            RandomNumberGenerator.Fill(tokenBytes);
            var token = Convert.ToBase64String(tokenBytes);

            _csrfTokens.Add(new CsrfToken
            {
                Token = token,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(config.SessionExpirationMinutes),
                IpAddress = ipAddress
            });

            return token;
        }

        private bool ValidateCsrfToken(string token, string ipAddress)
        {
            var config = Plugin.Instance.Configuration;
            if (!config.EnableCsrfProtection) return true;
            if (string.IsNullOrEmpty(token)) return false;

            var validation = _csrfTokens.Validate(token, ipAddress);
            if (validation == CsrfTokenValidation.IpAddressMismatch)
            {
                _logger.Warn($"[ExternalWebServer] CSRF token used from different IP.");
            }

            return validation == CsrfTokenValidation.Valid;
        }

        private bool CheckAccountLockout(string ipAddress)
        {
            var config = Plugin.Instance.Configuration;
            if (!config.EnableAccountLockout) return true;

            lock (_loginLock)
            {
                if (!_loginAttempts.ContainsKey(ipAddress))
                {
                    _loginAttempts[ipAddress] = new AccountLockout();
                    return true;
                }

                var lockout = _loginAttempts[ipAddress];

                if (lockout.LockedUntil.HasValue)
                {
                    if (DateTime.UtcNow < lockout.LockedUntil.Value)
                    {
                        LogAudit(ipAddress, "login_attempt_blocked", null, false, "Account is locked");
                        return false;
                    }
                    lockout.LockedUntil = null;
                    lockout.Attempts.Clear();
                }

                return true;
            }
        }

        private void RecordLoginAttempt(string ipAddress, bool success)
        {
            var config = Plugin.Instance.Configuration;
            if (!config.EnableAccountLockout) return;

            lock (_loginLock)
            {
                if (!_loginAttempts.ContainsKey(ipAddress))
                {
                    _loginAttempts[ipAddress] = new AccountLockout();
                }

                var lockout = _loginAttempts[ipAddress];
                lockout.Attempts.Add(new LoginAttempt
                {
                    Timestamp = DateTime.UtcNow,
                    Success = success
                });

                var recentAttempts = lockout.Attempts
                    .Where(a => !a.Success && (DateTime.UtcNow - a.Timestamp).TotalMinutes < config.LockoutWindowMinutes)
                    .ToList();

                if (recentAttempts.Count >= config.MaxFailedLoginAttempts)
                {
                    lockout.LockedUntil = DateTime.UtcNow.AddMinutes(config.LockoutDurationMinutes);
                    _logger.Warn($"[ExternalWebServer] Account locked for {ipAddress} due to {recentAttempts.Count} failed login attempts.");
                    LogAudit(ipAddress, "account_locked", null, false, $"Locked for {config.LockoutDurationMinutes} minutes");
                }

                if (success)
                {
                    lockout.Attempts.Clear();
                }
            }
        }

        private void LogAudit(string ipAddress, string action, string userId, bool success, string details = "")
        {
            var config = Plugin.Instance.Configuration;
            if (!config.EnableAuditLogging) return;

            lock (_auditLock)
            {
                _auditLog.Add(new AuditLogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    IpAddress = ipAddress,
                    Action = action,
                    UserId = userId,
                    Success = success,
                    Details = details
                });

                if (_auditLog.Count > config.MaxAuditLogEntries)
                {
                    _auditLog.RemoveAt(0);
                }
            }

            _logger.Info($"[Audit] {ipAddress} - {action} - Success: {success} - User: {userId ?? "N/A"} - {details}");
        }

        private void AddSecurityHeaders(HttpListenerResponse response)
        {
            var config = Plugin.Instance.Configuration;

            // Skip security headers if using reverse proxy (proxy will handle them)
            if (config.UseReverseProxy)
            {
                return;
            }

            if (config.EnableSecurityHeaders)
            {
                response.AddHeader("X-Content-Type-Options", "nosniff");
                response.AddHeader("X-Frame-Options", "DENY");
                response.AddHeader("X-XSS-Protection", "1; mode=block");
                response.AddHeader("Referrer-Policy", "strict-origin-when-cross-origin");

                if (!string.IsNullOrEmpty(config.ContentSecurityPolicy))
                {
                    response.AddHeader("Content-Security-Policy", config.ContentSecurityPolicy);
                }

                if (config.EnableHttps && config.EnableHsts)
                {
                    response.AddHeader("Strict-Transport-Security", $"max-age={config.HstsMaxAge}; includeSubDomains");
                }
            }
        }

        private async Task Listen(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _listener.IsListening)
            {
                HttpListenerContext context = null;
                try
                {
                    context = await _listener.GetContextAsync();
                    await _requestConcurrency.WaitAsync(token);
                    _ = Task.Run(() => ProcessRequestBounded(context, token));
                }
                catch (ObjectDisposedException) { break; }
                catch (HttpListenerException) { break; }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    context?.Response?.Close();
                    break;
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("[ExternalWebServer] Error processing request.", ex);
                    context?.Response?.Close();
                }
            }
        }

        private async Task ProcessRequestBounded(HttpListenerContext context, CancellationToken token)
        {
            try
            {
                await ProcessRequest(context, token);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[ExternalWebServer] Unhandled request failure.", ex);
                context.Response.Close();
            }
            finally
            {
                _requestConcurrency.Release();
            }
        }

        private async Task ProcessRequest(HttpListenerContext context, CancellationToken token)
        {
            var request = context.Request;
            var response = context.Response;

            if (request.Url == null)
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                response.OutputStream.Close();
                return;
            }

            var remoteAddress = request.RemoteEndPoint?.Address;
            var ipAddress = remoteAddress?.ToString() ?? "unknown";

            AddSecurityHeaders(response);

            if (!CheckRateLimit(ipAddress))
            {
                response.StatusCode = (int)HttpStatusCode.TooManyRequests;
                await WriteResponse(response, "{\"error\":\"Rate limit exceeded. Please try again later.\"}");
                response.OutputStream.Close();
                return;
            }

            try
            {
                var config = Plugin.Instance.Configuration;
                var origin = request.Headers["Origin"];

                // Set CORS headers (skip if using reverse proxy)
                if (!config.UseReverseProxy)
                {
                    if (!string.IsNullOrEmpty(config.AllowedCorsOrigins))
                    {
                        var allowedOrigins = config.AllowedCorsOrigins.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(o => o.Trim())
                            .ToList();

                        if (allowedOrigins.Contains("*"))
                        {
                            response.AddHeader("Access-Control-Allow-Origin", "*");
                        }
                        else if (!string.IsNullOrEmpty(origin) && allowedOrigins.Contains(origin))
                        {
                            response.AddHeader("Access-Control-Allow-Origin", origin);
                            response.AddHeader("Vary", "Origin");
                        }
                    }
                    else
                    {
                        // Default to localhost only
                        if (ExternalApiSecurityPolicy.IsAllowedDefaultCorsOrigin(origin))
                        {
                            response.AddHeader("Access-Control-Allow-Origin", origin);
                            response.AddHeader("Vary", "Origin");
                        }
                    }

                    response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                    response.AddHeader("Access-Control-Allow-Headers", "Content-Type, X-Session-Token, X-Auth-Password, X-CSRF-Token, X-Party-Password");
                    response.AddHeader("Access-Control-Expose-Headers", "X-Session-Token, X-CSRF-Token");
                }

                if (request.HttpMethod == "OPTIONS")
                {
                    response.StatusCode = (int)HttpStatusCode.OK;
                    response.OutputStream.Close();
                    return;
                }

                if (request.Url.AbsolutePath.StartsWith("/api/"))
                {
                    if (request.HttpMethod == "POST")
                    {
                        if (request.ContentLength64 > MaxRequestBodyBytes)
                        {
                            response.StatusCode = (int)HttpStatusCode.RequestEntityTooLarge;
                            await WriteResponse(response, "{\"error\":\"Request too large\"}");
                            return;
                        }

                        string requestBody;
                        try
                        {
                            requestBody = await ExternalRequestBodyReader.ReadAsync(
                                request.InputStream,
                                request.ContentEncoding,
                                MaxRequestBodyBytes,
                                token);
                        }
                        catch (RequestBodyTooLargeException)
                        {
                            response.StatusCode = (int)HttpStatusCode.RequestEntityTooLarge;
                            await WriteResponse(response, "{\"error\":\"Request too large\"}");
                            return;
                        }

                        await HandleApiRequest(request, response, requestBody, ipAddress, remoteAddress, token);
                    }
                    else
                    {
                        await HandleApiRequest(request, response, null, ipAddress, remoteAddress, token);
                    }
                    return;
                }

                await HandleFileRequest(request.Url.AbsolutePath, response);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"[ExternalWebServer] Error during request:", ex);
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                await WriteResponse(response, "{\"error\":\"An internal error occurred\"}");
            }
            finally
            {
                response.OutputStream.Close();
            }
        }

        private async Task HandleFileRequest(string path, HttpListenerResponse response)
        {
            var resourcePath = (path == "/" || path == "/index.html") ? "external.html" : path.TrimStart('/');
            var resourceName = $"{GetType().Namespace}.Configuration.{resourcePath}";
            var assembly = GetType().GetTypeInfo().Assembly;

            _logger.Debug($"[ExternalWebServer] Attempting to serve: {resourceName}");

            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    _logger.Warn($"[ExternalWebServer] Resource not found: {resourceName}");
                    response.StatusCode = (int)HttpStatusCode.NotFound;
                    await WriteResponse(response, "404 Not Found");
                    return;
                }

                response.StatusCode = (int)HttpStatusCode.OK;
                response.ContentType = GetContentType(resourcePath);
                await stream.CopyToAsync(response.OutputStream);
            }
        }

        private async Task HandleApiRequest(
            HttpListenerRequest request,
            HttpListenerResponse response,
            string requestBody,
            string ipAddress,
            IPAddress remoteAddress,
            CancellationToken token)
        {
            var path = request.Url.AbsolutePath;
            var sessionToken = request.Headers["X-Session-Token"];
            var authentikUsername = request.Headers["X-Authentik-Username"];
            var config = Plugin.Instance.Configuration;
            var isPublicEndpoint = ExternalApiSecurityPolicy.IsPublicEndpoint(
                request.HttpMethod,
                path);
            var hasValidSession = !isPublicEndpoint
                && ValidateSession(sessionToken, ipAddress);
            var hasValidCsrfToken = !isPublicEndpoint
                && string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase)
                && ValidateCsrfToken(request.Headers["X-CSRF-Token"], ipAddress);
            var securityRequest = new ExternalApiSecurityRequest
            {
                Method = request.HttpMethod,
                Path = path,
                HasValidSession = hasValidSession,
                UseReverseProxy = config.UseReverseProxy,
                RemoteAddress = remoteAddress,
                AuthentikUsername = authentikUsername,
                CsrfProtectionEnabled = config.EnableCsrfProtection,
                HasValidCsrfToken = hasValidCsrfToken
            };
            var securityDecision = ExternalApiSecurityPolicy.Evaluate(securityRequest);

            if (securityDecision == ExternalApiSecurityDecision.AuthenticationRequired)
            {
                response.StatusCode = (int)HttpStatusCode.Unauthorized;
                await WriteResponse(response, "{\"error\":\"Invalid or expired session\"}");
                return;
            }

            if (securityDecision == ExternalApiSecurityDecision.CsrfTokenRequired)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                await WriteResponse(response, "{\"error\":\"Invalid or expired CSRF token\"}");
                return;
            }

            // Public endpoints (no authentication required)
            if (request.HttpMethod == "POST" && path == "/api/auth/login" && requestBody != null)
            {
                await HandleLogin(response, requestBody, ipAddress);
                return;
            }
            else if (request.HttpMethod == "GET" && path == "/api/auth/user")
            {
                var trustedSso = ExternalApiSecurityPolicy.IsTrustedSso(securityRequest);
                var username = trustedSso ? authentikUsername ?? "" : "";
                var email = trustedSso ? request.Headers["X-Authentik-Email"] ?? "" : "";
                var displayName = trustedSso ? request.Headers["X-Authentik-Name"] ?? "" : "";
                var groups = trustedSso ? request.Headers["X-Authentik-Groups"] ?? "" : "";
                var csrfToken = trustedSso ? GenerateCsrfToken(ipAddress) : null;
                if (!string.IsNullOrEmpty(csrfToken))
                {
                    response.AddHeader("X-CSRF-Token", csrfToken);
                }
                response.AddHeader("Cache-Control", "no-store");
                response.AddHeader("Pragma", "no-cache");
                var result = new
                {
                    username = username,
                    email = email,
                    displayName = displayName,
                    groups = groups,
                    authenticated = trustedSso,
                    csrfToken = csrfToken
                };
                var json = _jsonSerializer.SerializeToString(result);
                response.StatusCode = (int)HttpStatusCode.OK;
                response.ContentType = "application/json";
                await WriteResponse(response, json);
                return;
            }
            else if (request.HttpMethod == "GET" && path == "/api/config/external-url")
            {
                var result = new
                {
                    externalServerUrl = config.ExternalServerUrl ?? "",
                    embyServerUrl = !string.IsNullOrWhiteSpace(config.ExternalServerUrl)
                        ? config.ExternalServerUrl
                        : config.EmbyServerUrl ?? ""
                };

                var json = _jsonSerializer.SerializeToString(result);
                response.StatusCode = (int)HttpStatusCode.OK;
                response.ContentType = "application/json";
                await WriteResponse(response, json);
                return;
            }
            else if (request.HttpMethod == "POST" && path == "/api/users/lookup" && requestBody != null)
            {
                await HandleUserLookup(response, requestBody);
                return;
            }

            if (path.StartsWith("/api/emby", StringComparison.Ordinal))
            {
                if (!ExternalApiSecurityPolicy.IsAllowedEmbyProxyRoute(request.HttpMethod, path))
                {
                    response.StatusCode = string.Equals(
                        request.HttpMethod,
                        "GET",
                        StringComparison.OrdinalIgnoreCase)
                        ? (int)HttpStatusCode.NotFound
                        : (int)HttpStatusCode.MethodNotAllowed;
                    await WriteResponse(response, "{\"error\":\"Emby proxy route not allowed\"}");
                    return;
                }

                await HandleEmbyProxy(request, response, token);
                return;
            }
            else if (request.HttpMethod == "GET" && path == "/api/parties")
            {
                // Party password moved to header
                var password = request.Headers["X-Party-Password"];
                await HandleGetParties(response, password);
            }
            else if (request.HttpMethod == "POST" && path == "/api/parties/create" && requestBody != null)
            {
                await HandleCreateParty(response, requestBody, ipAddress);
            }
            else if (request.HttpMethod == "POST" && path == "/api/parties/delete" && requestBody != null)
            {
                await HandleDeleteParty(response, requestBody, ipAddress);
            }
            else if (request.HttpMethod == "POST" && path == "/api/parties/start" && requestBody != null)
            {
                await HandleStartParty(response, requestBody, ipAddress);
            }
            else if (request.HttpMethod == "POST" && path == "/api/auth/logout")
            {
                if (!string.IsNullOrEmpty(sessionToken))
                {
                    lock (_sessionLock)
                    {
                        _activeSessions.Remove(sessionToken);
                    }
                }
                response.StatusCode = (int)HttpStatusCode.OK;
                await WriteResponse(response, "{\"success\":true}");
            }
            else
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                await WriteResponse(response, "{\"error\":\"Endpoint not found\"}");
            }
        }

        private async Task HandleStartParty(
            HttpListenerResponse response,
            string requestBody,
            string ipAddress)
        {
            Dictionary<string, object> request;
            try
            {
                request = _jsonSerializer.DeserializeFromString<Dictionary<string, object>>(requestBody);
            }
            catch (Exception)
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                await WriteResponse(response, "{\"error\":\"Invalid request body\"}");
                return;
            }

            var partyId = request != null && request.ContainsKey("partyId")
                ? request["partyId"]?.ToString()
                : null;
            if (string.IsNullOrWhiteSpace(partyId) || !ValidateInput(partyId, 100))
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                await WriteResponse(response, "{\"error\":\"Party ID is required\"}");
                return;
            }

            try
            {
                var started = await Plugin.Instance.WaitingRoomStarts
                    .StartAsync(partyId)
                    .ConfigureAwait(false);
                if (!started)
                {
                    response.StatusCode = (int)HttpStatusCode.Conflict;
                    await WriteResponse(
                        response,
                        "{\"error\":\"Party is inactive or no longer waiting\"}");
                    return;
                }

                LogAudit(ipAddress, "start_party", partyId, true, "Waiting room started");
                response.StatusCode = (int)HttpStatusCode.OK;
                await WriteResponse(response, "{\"success\":true}");
            }
            catch (InvalidOperationException ex)
            {
                _logger.Warn($"[ExternalWebServer] Cannot start party {partyId}: {ex.Message}");
                response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                await WriteResponse(response, "{\"error\":\"Playback runtime is unavailable\"}");
            }
        }

        private async Task HandleLogin(HttpListenerResponse response, string requestBody, string ipAddress)
        {
            try
            {
                if (!CheckAccountLockout(ipAddress))
                {
                    response.StatusCode = (int)HttpStatusCode.Forbidden;
                    await WriteResponse(response, "{\"error\":\"Account temporarily locked due to too many failed attempts\"}");
                    return;
                }

                var request = _jsonSerializer.DeserializeFromString<Dictionary<string, object>>(requestBody);
                var adminPassword = request.ContainsKey("adminPassword") ? request["adminPassword"]?.ToString() : null;

                if (!ValidateInput(adminPassword, 200))
                {
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await WriteResponse(response, "{\"error\":\"Invalid input\"}");
                    LogAudit(ipAddress, "login", null, false, "Invalid input");
                    return;
                }

                var config = Plugin.Instance.Configuration;

                if (string.IsNullOrEmpty(config.AdminPasswordHash) || string.IsNullOrEmpty(adminPassword) ||
                    !PasswordHelper.VerifyPassword(adminPassword, config.AdminPasswordHash))
                {
                    RecordLoginAttempt(ipAddress, false);
                    LogAudit(ipAddress, "login", null, false, "Invalid credentials");

                    // Add delay to prevent brute force
                    await Task.Delay(1000);
                    response.StatusCode = (int)HttpStatusCode.Unauthorized;
                    await WriteResponse(response, "{\"error\":\"Invalid credentials\"}");
                    return;
                }

                RecordLoginAttempt(ipAddress, true);
                var token = CreateSession(ipAddress);
                var csrfToken = GenerateCsrfToken(ipAddress);

                LogAudit(ipAddress, "login", "admin", true, "Successful login");

                response.AddHeader("X-Session-Token", token);
                if (!string.IsNullOrEmpty(csrfToken))
                {
                    response.AddHeader("X-CSRF-Token", csrfToken);
                }
                response.AddHeader("Cache-Control", "no-store");
                response.AddHeader("Pragma", "no-cache");

                response.StatusCode = (int)HttpStatusCode.OK;
                response.ContentType = "application/json";
                await WriteResponse(response, _jsonSerializer.SerializeToString(new {
                    success = true,
                    token = token,
                    csrfToken = csrfToken,
                    expiresIn = config.SessionExpirationMinutes * 60
                }));
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[ExternalWebServer] Error during login", ex);
                LogAudit(ipAddress, "login", null, false, "Exception occurred");
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                await WriteResponse(response, "{\"error\":\"Authentication failed\"}");
            }
        }

        private async Task HandleEmbyProxy(
            HttpListenerRequest request,
            HttpListenerResponse response,
            CancellationToken token)
        {
            try
            {
                var config = Plugin.Instance.Configuration;
                var apiKey = config.EmbyApiKey;

                if (string.IsNullOrEmpty(apiKey))
                {
                    response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                    await WriteResponse(response, "{\"error\":\"Service not configured\"}");
                    return;
                }

                var embyPath = request.Url.AbsolutePath.Substring("/api/emby".Length);

                if (string.IsNullOrEmpty(embyPath) || embyPath.Contains("..") || embyPath.Contains("//"))
                {
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await WriteResponse(response, "{\"error\":\"Invalid path\"}");
                    return;
                }

                using (var client = new System.Net.Http.HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(30);

                    var embyUrl = EmbyServerAddress.Build(config.EmbyServerUrl, $"emby{embyPath}");
                    var queryParams = new List<string>();

                    if (request.QueryString.Count > 0)
                    {
                        foreach (var key in request.QueryString.AllKeys)
                        {
                            var value = key == null ? null : request.QueryString[key];
                            if (string.IsNullOrEmpty(key)
                                || !ValidateInput(key, 100)
                                || !ValidateInput(value, 500))
                            {
                                response.StatusCode = (int)HttpStatusCode.BadRequest;
                                await WriteResponse(response, "{\"error\":\"Invalid query parameters\"}");
                                return;
                            }
                            queryParams.Add(
                                $"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value ?? string.Empty)}");
                        }
                    }

                    if (queryParams.Count > 0)
                    {
                        embyUrl += "?" + string.Join("&", queryParams);
                    }

                    using (var httpRequest = new System.Net.Http.HttpRequestMessage(
                        System.Net.Http.HttpMethod.Get,
                        embyUrl))
                    {
                        httpRequest.Headers.Add("X-Emby-Token", apiKey);

                        using (var embyResponse = await client.SendAsync(httpRequest, token))
                        {
                            var content = await embyResponse.Content.ReadAsStringAsync();

                            response.StatusCode = (int)embyResponse.StatusCode;
                            response.ContentType = "application/json";
                            await WriteResponse(response, content);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[ExternalWebServer] Error proxying to Emby", ex);
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                await WriteResponse(response, "{\"error\":\"Proxy request failed\"}");
            }
        }

        private async Task HandleUserLookup(HttpListenerResponse response, string requestBody)
        {
            try
            {
                var request = _jsonSerializer.DeserializeFromString<Dictionary<string, object>>(requestBody);
                var userId = request.ContainsKey("userId") ? request["userId"]?.ToString() : null;

                if (!ValidateInput(userId, 100))
                {
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await WriteResponse(response, "{\"error\":\"Invalid input\"}");
                    return;
                }

                if (string.IsNullOrEmpty(userId))
                {
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await WriteResponse(response, "{\"error\":\"userId is required\"}");
                    return;
                }

                var config = Plugin.Instance.Configuration;
                var embyServerUrl = EmbyServerAddress.GetBaseUrl(config.EmbyServerUrl);

                var apiKey = config.EmbyApiKey;

                using (var httpClient = new System.Net.Http.HttpClient())
                {
                    httpClient.Timeout = TimeSpan.FromSeconds(10);
                    var httpRequest = new System.Net.Http.HttpRequestMessage(
                        System.Net.Http.HttpMethod.Get,
                        $"{embyServerUrl}/emby/Users/{Uri.EscapeDataString(userId)}"
                    );
                    httpRequest.Headers.Add("X-Emby-Token", apiKey);

                    var userResponse = await httpClient.SendAsync(httpRequest);
                    var userJson = await userResponse.Content.ReadAsStringAsync();
                    var userData = _jsonSerializer.DeserializeFromString<Dictionary<string, object>>(userJson);
                    var userName = userData.ContainsKey("Name") ? userData["Name"]?.ToString() : "Unknown";

                    var result = new { userId = userId, userName = userName };
                    var resultJson = _jsonSerializer.SerializeToString(result);
                    response.StatusCode = (int)HttpStatusCode.OK;
                    response.ContentType = "application/json";
                    await WriteResponse(response, resultJson);
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[ExternalWebServer] Error looking up user", ex);
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                await WriteResponse(response, "{\"error\":\"User lookup failed\"}");
            }
        }

        private async Task HandleGetParties(HttpListenerResponse response, string password)
        {
            try
            {
                var parties = new List<object>();
                List<WatchPartyItem> partySnapshot;

                lock (Plugin.Instance.ConfigurationSyncRoot)
                {
                    partySnapshot = Plugin.Instance.Configuration.WatchParties?.ToList()
                        ?? new List<WatchPartyItem>();
                }

                foreach (var party in partySnapshot)
                {
                    if (!string.IsNullOrEmpty(party.PasswordHash))
                    {
                        if (string.IsNullOrEmpty(password) || !PasswordHelper.VerifyPassword(password, party.PasswordHash))
                        {
                            continue;
                        }
                    }

                    parties.Add(new
                    {
                        Id = party.Id,
                        ItemId = party.ItemId,
                        ItemName = party.ItemName,
                        ItemType = party.ItemType,
                        SeriesName = party.SeriesName,
                        IsSeriesParty = party.IsSeriesParty,
                        EpisodeCount = party.EpisodeQueue?.Count ?? 0,
                        CurrentEpisodeIndex = party.CurrentEpisodeIndex,
                        IsActive = party.IsActive,
                        IsWaitingRoom = party.IsWaitingRoom,
                        ParticipantCount = Plugin.Instance.PartyParticipants.DistinctUserCount(party.Id),
                        MaxParticipants = party.MaxParticipants,
                        HostUserName = !string.IsNullOrEmpty(party.MasterUserId) ? party.MasterUserId : "Not set",
                        CurrentPositionTicks = party.CurrentPositionTicks,
                        IsPlaying = party.IsPlaying,
                        RequiresPassword = !string.IsNullOrEmpty(party.PasswordHash)
                    });
                }

                var json = _jsonSerializer.SerializeToString(new { Parties = parties });
                response.StatusCode = (int)HttpStatusCode.OK;
                response.ContentType = "application/json";
                await WriteResponse(response, json);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[ExternalWebServer] Error getting parties", ex);
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                await WriteResponse(response, "{\"error\":\"Failed to retrieve parties\"}");
            }
        }

        private async Task HandleCreateParty(HttpListenerResponse response, string requestBody, string ipAddress)
        {
            try
            {
                var request = _jsonSerializer.DeserializeFromString<Dictionary<string, object>>(requestBody);

                // Validate inputs
                var itemName = request.ContainsKey("itemName") ? request["itemName"]?.ToString() : null;
                var libraryId = request.ContainsKey("libraryId") ? request["libraryId"]?.ToString() : null;
                var itemId = request.ContainsKey("itemId") ? request["itemId"]?.ToString() : null;
                var masterUserId = request.ContainsKey("masterUserId") ? request["masterUserId"]?.ToString() : null;
                if (string.IsNullOrWhiteSpace(itemName)
                    || string.IsNullOrWhiteSpace(libraryId)
                    || string.IsNullOrWhiteSpace(itemId)
                    || string.IsNullOrWhiteSpace(masterUserId)
                    || !ValidateInput(itemName, 200)
                    || !ValidateInput(libraryId, 100)
                    || !ValidateInput(itemId, 100)
                    || !ValidateInput(masterUserId, 100))
                {
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await WriteResponse(response, "{\"error\":\"Library, item, item name, and master user are required\"}");
                    LogAudit(ipAddress, "create_party", null, false, "Missing or invalid required fields");
                    return;
                }

                var allowedUserIds = new List<string>();
                if (request.ContainsKey("allowedUserIds") && request["allowedUserIds"] != null)
                {
                    var userIdsArray = request["allowedUserIds"] as object[];
                    if (userIdsArray != null)
                    {
                        allowedUserIds = userIdsArray
                            .Select(x => x?.ToString())
                            .Where(x => !string.IsNullOrEmpty(x) && ValidateInput(x, 100))
                            .ToList();
                    }
                }

                var isSeriesParty = request.ContainsKey("isSeriesParty") && Convert.ToBoolean(request["isSeriesParty"]);
                var episodeQueue = new List<WatchPartyEpisode>();
                var currentEpisodeIndex = -1;
                string currentEpisodeId = null;

                if (isSeriesParty)
                {
                    try
                    {
                        if (!request.ContainsKey("episodeQueue") || request["episodeQueue"] == null)
                        {
                            throw new InvalidOperationException("Episode queue is required");
                        }

                        var episodeQueueJson = _jsonSerializer.SerializeToString(request["episodeQueue"]);
                        episodeQueue = _jsonSerializer.DeserializeFromString<List<WatchPartyEpisode>>(episodeQueueJson)
                            ?? new List<WatchPartyEpisode>();
                        currentEpisodeIndex = request.ContainsKey("currentEpisodeIndex")
                            ? Convert.ToInt32(request["currentEpisodeIndex"])
                            : -1;
                        currentEpisodeId = request.ContainsKey("currentEpisodeId")
                            ? request["currentEpisodeId"]?.ToString()
                            : null;
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"[ExternalWebServer] Invalid Series Party queue payload: {ex.Message}");
                        response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await WriteResponse(response, "{\"error\":\"Invalid Series Party episode queue\"}");
                        return;
                    }

                    var distinctItemIds = new HashSet<string>(StringComparer.Ordinal);
                    var queueIsValid = episodeQueue.Count > 0
                        && episodeQueue.Count <= 10000
                        && currentEpisodeIndex >= 0
                        && currentEpisodeIndex < episodeQueue.Count
                        && episodeQueue.All(episode => episode != null
                            && ValidateInput(episode.ItemId, 100)
                            && ValidateInput(episode.ItemName, 200)
                            && ValidateInput(episode.SeasonId, 100)
                            && episode.SeasonNumber > 0
                            && episode.EpisodeNumber >= 0
                            && distinctItemIds.Add(episode.ItemId));

                    if (!queueIsValid
                        || !string.Equals(currentEpisodeId, episodeQueue[currentEpisodeIndex].ItemId, StringComparison.Ordinal)
                        || !request.ContainsKey("itemId")
                        || !string.Equals(request["itemId"]?.ToString(), currentEpisodeId, StringComparison.Ordinal))
                    {
                        response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await WriteResponse(response, "{\"error\":\"Invalid Series Party starting point\"}");
                        return;
                    }
                }

                var newParty = new WatchPartyItem
                {
                    Id = Guid.NewGuid().ToString(),
                    LibraryId = libraryId,
                    ItemId = itemId,
                    ItemName = itemName,
                    ItemType = request.ContainsKey("itemType") && ValidateInput(request["itemType"]?.ToString(), 50)
                        ? request["itemType"]?.ToString() : "Movie",
                    SeriesId = request.ContainsKey("seriesId") && ValidateInput(request["seriesId"]?.ToString(), 100)
                        ? request["seriesId"]?.ToString() : null,
                    SeriesName = request.ContainsKey("seriesName") && ValidateInput(request["seriesName"]?.ToString(), 200)
                        ? request["seriesName"]?.ToString() : null,
                    SeasonId = request.ContainsKey("seasonId") && ValidateInput(request["seasonId"]?.ToString(), 100)
                        ? request["seasonId"]?.ToString() : null,
                    IsSeriesParty = isSeriesParty,
                    EpisodeQueue = episodeQueue,
                    CurrentEpisodeIndex = currentEpisodeIndex,
                    CurrentEpisodeId = currentEpisodeId,
                    IsActive = request.ContainsKey("isActive") ? Convert.ToBoolean(request["isActive"]) : false,
                    CurrentPositionTicks = 0,
                    IsPlaying = false,
                    MaxParticipants = request.ContainsKey("maxParticipants") ?
                        Math.Min(Math.Max(Convert.ToInt32(request["maxParticipants"]), 1), 1000) : 50,
                    AllowedUserIds = allowedUserIds,
                    MasterUserId = masterUserId,
                    IsWaitingRoom = request.ContainsKey("isWaitingRoom") ? Convert.ToBoolean(request["isWaitingRoom"]) : false,
                    AutoStartWhenReady = request.ContainsKey("autoStartWhenReady") ? Convert.ToBoolean(request["autoStartWhenReady"]) : false,
                    MinReadyCount = request.ContainsKey("minReadyCount") ?
                        Math.Max(Convert.ToInt32(request["minReadyCount"]), 1) : 1,
                    PauseControl = request.ContainsKey("pauseControl") && ValidateInput(request["pauseControl"]?.ToString(), 20)
                        ? request["pauseControl"]?.ToString() : "Anyone",
                    SyncToleranceSeconds = request.ContainsKey("syncToleranceSeconds") ?
                        Math.Max(Convert.ToInt32(request["syncToleranceSeconds"]), 1) : 2,
                    MaxBufferThresholdSeconds = request.ContainsKey("maxBufferThresholdSeconds") ?
                        Math.Max(Convert.ToInt32(request["maxBufferThresholdSeconds"]), 1) : 30,
                    AutoKickInactiveMinutes = request.ContainsKey("autoKickInactiveMinutes") ? Convert.ToBoolean(request["autoKickInactiveMinutes"]) : false,
                    InactiveTimeoutMinutes = request.ContainsKey("inactiveTimeoutMinutes") ?
                        Math.Max(Convert.ToInt32(request["inactiveTimeoutMinutes"]), 1) : 15,
                    CreatedDate = DateTime.UtcNow
                };

                var partyPassword = request.ContainsKey("password") ? request["password"]?.ToString() : null;
                if (!string.IsNullOrEmpty(partyPassword) && ValidateInput(partyPassword, 200))
                {
                    newParty.PasswordHash = PasswordHelper.HashPassword(partyPassword);
                }

                var hasBindingConflict = false;
                var plugin = Plugin.Instance;
                lock (plugin.ConfigurationSyncRoot)
                {
                    var config = plugin.Configuration;
                    WatchPartyConfigurationPolicy.Normalize(newParty);
                    hasBindingConflict = WatchPartyItemMatcher.HasActiveBindingConflict(
                        config.WatchParties.Concat(new[] { newParty }));
                    if (!hasBindingConflict)
                    {
                        config.WatchParties.Add(newParty);
                        try
                        {
                            plugin.UpdateConfiguration(config);
                        }
                        catch
                        {
                            config.WatchParties.Remove(newParty);
                            throw;
                        }
                    }
                }

                if (hasBindingConflict)
                {
                    response.StatusCode = (int)HttpStatusCode.Conflict;
                    await WriteResponse(
                        response,
                        "{\"error\":\"This item already belongs to another active watch party\"}");
                    LogAudit(ipAddress, "create_party", newParty.MasterUserId, false, "Item binding conflict");
                    return;
                }

                LogAudit(ipAddress, "create_party", newParty.MasterUserId, true, $"Party: {newParty.ItemName}");

                var resultJson = _jsonSerializer.SerializeToString(new { success = true, partyId = newParty.Id });
                response.StatusCode = (int)HttpStatusCode.OK;
                response.ContentType = "application/json";
                await WriteResponse(response, resultJson);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[ExternalWebServer] Error creating party", ex);
                LogAudit(ipAddress, "create_party", null, false, "Exception occurred");
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                await WriteResponse(response, "{\"error\":\"Failed to create party\"}");
            }
        }

        private async Task HandleDeleteParty(HttpListenerResponse response, string requestBody, string ipAddress)
        {
            try
            {
                var request = _jsonSerializer.DeserializeFromString<Dictionary<string, object>>(requestBody);
                var partyId = request.ContainsKey("partyId") ? request["partyId"]?.ToString() : null;

                if (!ValidateInput(partyId, 100))
                {
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await WriteResponse(response, "{\"error\":\"Invalid input\"}");
                    LogAudit(ipAddress, "delete_party", null, false, "Invalid input");
                    return;
                }

                if (string.IsNullOrEmpty(partyId))
                {
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await WriteResponse(response, "{\"error\":\"Party ID is required\"}");
                    LogAudit(ipAddress, "delete_party", null, false, "Missing party ID");
                    return;
                }

                var plugin = Plugin.Instance;
                WatchPartyItem party;
                lock (plugin.ConfigurationSyncRoot)
                {
                    var config = plugin.Configuration;
                    party = config.WatchParties.FirstOrDefault(p => p.Id == partyId);
                    if (party != null)
                    {
                        var originalIndex = config.WatchParties.IndexOf(party);
                        config.WatchParties.RemoveAt(originalIndex);
                        try
                        {
                            plugin.UpdateConfiguration(config);
                        }
                        catch
                        {
                            config.WatchParties.Insert(originalIndex, party);
                            throw;
                        }
                    }
                }

                if (party == null)
                {
                    response.StatusCode = (int)HttpStatusCode.NotFound;
                    await WriteResponse(response, "{\"error\":\"Party not found\"}");
                    LogAudit(ipAddress, "delete_party", null, false, "Party not found");
                    return;
                }

                var partyName = party.ItemName;

                LogAudit(ipAddress, "delete_party", party.MasterUserId, true, $"Party: {partyName}");

                var resultJson = _jsonSerializer.SerializeToString(new { success = true });
                response.StatusCode = (int)HttpStatusCode.OK;
                response.ContentType = "application/json";
                await WriteResponse(response, resultJson);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[ExternalWebServer] Error deleting party", ex);
                LogAudit(ipAddress, "delete_party", null, false, "Exception occurred");
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                await WriteResponse(response, "{\"error\":\"Failed to delete party\"}");
            }
        }

        private string GetContentType(string path)
        {
            if (path.EndsWith(".html")) return "text/html";
            if (path.EndsWith(".js")) return "application/javascript";
            if (path.EndsWith(".css")) return "text/css";
            if (path.EndsWith(".json")) return "application/json";
            return "application/octet-stream";
        }

        private async Task WriteResponse(HttpListenerResponse response, string content)
        {
            var buffer = Encoding.UTF8.GetBytes(content);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
        }

    }

    public class CsrfToken
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

    public class LoginAttempt
    {
        public DateTime Timestamp { get; set; }
        public bool Success { get; set; }
    }

    public class AccountLockout
    {
        public List<LoginAttempt> Attempts { get; set; } = new List<LoginAttempt>();
        public DateTime? LockedUntil { get; set; }
    }

    public class AuditLogEntry
    {
        public DateTime Timestamp { get; set; }
        public string IpAddress { get; set; }
        public string Action { get; set; }
        public string UserId { get; set; }
        public bool Success { get; set; }
        public string Details { get; set; }
    }
}
