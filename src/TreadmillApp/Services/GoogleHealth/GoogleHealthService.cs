using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using TreadmillApp.Services;

namespace TreadmillApp.Services.GoogleHealth;

/// <summary>
/// Public API to the Google Health API: connect (one-time OAuth), upload
/// completed walks, disconnect, forget credentials. All token refresh is
/// handled internally.
///
/// The app runs as an unverified ("Testing" mode) Google Cloud OAuth client,
/// so refresh tokens expire roughly every 7 days. Rather than just going
/// dark, a failed refresh automatically reopens the browser to re-consent
/// (guarded so it only pops once per app run — a second failure in the same
/// run falls back to a normal transient failure instead of spawning another
/// browser tab).
/// </summary>
public sealed class GoogleHealthService : IDisposable
{
    private const string AuthorizeEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint     = "https://oauth2.googleapis.com/token";
    private const string UploadEndpoint    = "https://health.googleapis.com/v4/users/me/dataTypes/exercise/dataPoints";
    private const string Scope             = "https://www.googleapis.com/auth/googlehealth.activity_and_fitness.writeonly";

    private readonly GoogleHealthSecureStorage _storage = new();
    private readonly HttpClient                _http    = new() { Timeout = TimeSpan.FromSeconds(90) };

    private bool _reauthAttemptedThisRun;

    public event EventHandler<string>? Log;

    private GoogleHealthTokens? _cachedTokens;

    public bool HasCredentials => _storage.LoadCredentials() != null;
    public bool IsConnected    => CurrentTokens != null;

    private GoogleHealthTokens? CurrentTokens => _cachedTokens ??= _storage.LoadTokens();

    public GoogleHealthCredentials? GetCredentials() => _storage.LoadCredentials();
    public void SaveCredentials(GoogleHealthCredentials creds) => _storage.SaveCredentials(creds);

    public void Disconnect()
    {
        _storage.ClearTokens();
        _cachedTokens = null;
    }

    public void ForgetCredentials()
    {
        _storage.ClearTokens();
        _storage.ClearCredentials();
        _cachedTokens = null;
    }

    // =========================================================================
    // OAuth — one-time (and re-consent) authorization flow
    // =========================================================================

    /// <summary>
    /// Runs the OAuth authorization flow: spins up a localhost listener on a
    /// random ephemeral port, opens the user's browser to Google's consent
    /// page, exchanges the returned code for tokens, and persists them.
    /// </summary>
    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        var creds = _storage.LoadCredentials();
        if (creds == null)
        {
            LogMsg("No Client ID/Secret saved. Enter them on the Google Health tab first.");
            return false;
        }

        // 1. Bind a random local port for the OAuth callback
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port        = ((IPEndPoint)listener.LocalEndpoint).Port;
        var redirectUri = $"http://localhost:{port}/callback";
        var state       = Guid.NewGuid().ToString("N");

        // 2. Open the browser to the authorize URL. access_type=offline +
        // prompt=consent guarantee a fresh refresh token every time, since
        // re-consent happens roughly weekly for this unverified app.
        var authUrl =
            $"{AuthorizeEndpoint}" +
            $"?client_id={Uri.EscapeDataString(creds.ClientId)}" +
            $"&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&scope={Uri.EscapeDataString(Scope)}" +
            $"&access_type=offline" +
            $"&prompt=consent" +
            $"&state={state}";

        try
        {
            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogMsg($"Could not open browser: {ex.Message}");
            listener.Stop();
            return false;
        }

        // 3. Wait for the redirect (timeout after 5 minutes)
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

        try
        {
            using var tcp = await listener.AcceptTcpClientAsync(timeoutCts.Token);
            var (code, returnedState, error) = await ReadCallbackAsync(tcp, timeoutCts.Token);

            await WriteBrowserResponseAsync(tcp, error == null
                ? "<h2>Connected!</h2><p>You can close this tab and return to Treadmill Tracker.</p>"
                : $"<h2>Authorization failed</h2><p>{HtmlEncode(error)}</p>");

            if (error != null)          { LogMsg($"Google Health authorization failed: {error}"); return false; }
            if (returnedState != state) { LogMsg("Google Health authorization failed: state mismatch (possible CSRF)."); return false; }
            if (string.IsNullOrEmpty(code)) { LogMsg("Google Health authorization failed: no code returned."); return false; }

            // 4. Exchange the code for tokens
            var tokens = await ExchangeCodeAsync(creds, code!, redirectUri, ct);
            if (tokens == null) return false;

            _storage.SaveTokens(tokens);
            _cachedTokens = tokens;
            LogMsg("Connected to Google Health.");
            return true;
        }
        catch (OperationCanceledException)
        {
            LogMsg("Google Health authorization timed out (no response within 5 minutes).");
            return false;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<(string? code, string? state, string? error)> ReadCallbackAsync(
        TcpClient tcp, CancellationToken ct)
    {
        var stream = tcp.GetStream();
        var buf = new byte[8192];
        var read = await stream.ReadAsync(buf, ct);
        var requestText = Encoding.UTF8.GetString(buf, 0, read);

        // Request line: "GET /callback?code=...&state=... HTTP/1.1"
        var firstLine = requestText.Split("\r\n", 2)[0];
        var parts = firstLine.Split(' ');
        if (parts.Length < 2) return (null, null, "malformed request");

        var pathAndQuery = parts[1];
        var qIdx = pathAndQuery.IndexOf('?');
        if (qIdx < 0) return (null, null, "no query parameters");

        var query = ParseQueryString(pathAndQuery[(qIdx + 1)..]);
        query.TryGetValue("code",  out var code);
        query.TryGetValue("state", out var state);
        query.TryGetValue("error", out var error);
        return (code, state, error);
    }

    private static Dictionary<string, string> ParseQueryString(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) { dict[Uri.UnescapeDataString(pair)] = ""; continue; }
            var key = Uri.UnescapeDataString(pair[..eq]);
            var val = Uri.UnescapeDataString(pair[(eq + 1)..]);
            dict[key] = val;
        }
        return dict;
    }

    private static string HtmlEncode(string s)
        => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static async Task WriteBrowserResponseAsync(TcpClient tcp, string bodyHtml)
    {
        var html = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>Treadmill Tracker</title>" +
                   $"<style>body{{font-family:Segoe UI,sans-serif;background:#1E1E2E;color:#fff;text-align:center;padding:48px}}</style>" +
                   $"</head><body>{bodyHtml}</body></html>";
        var body  = Encoding.UTF8.GetBytes(html);
        var resp  = $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(resp));
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    // =========================================================================
    // Token exchange / refresh
    // =========================================================================

    private async Task<GoogleHealthTokens?> ExchangeCodeAsync(
        GoogleHealthCredentials creds, string code, string redirectUri, CancellationToken ct)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"]     = creds.ClientId,
            ["client_secret"] = creds.ClientSecret,
            ["code"]          = code,
            ["redirect_uri"]  = redirectUri,
            ["grant_type"]    = "authorization_code",
        });

        try
        {
            var resp = await _http.PostAsync(TokenEndpoint, form, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                LogMsg($"Token exchange failed ({(int)resp.StatusCode}): {body}");
                return null;
            }

            var tokens = ParseTokenResponse(body);
            if (tokens != null && string.IsNullOrEmpty(tokens.RefreshToken))
                LogMsg("Warning: Google did not return a refresh token on initial connect. Re-auth in ~1 hour will fail — try disconnecting and reconnecting.");
            return tokens;
        }
        catch (Exception ex)
        {
            LogMsg($"Token exchange error: {ex.Message}");
            return null;
        }
    }

    private async Task<GoogleHealthTokens?> RefreshAsync(GoogleHealthTokens current, CancellationToken ct)
    {
        var creds = _storage.LoadCredentials();
        if (creds == null) return null;

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"]     = creds.ClientId,
            ["client_secret"] = creds.ClientSecret,
            ["refresh_token"] = current.RefreshToken,
            ["grant_type"]    = "refresh_token",
        });

        try
        {
            var resp = await _http.PostAsync(TokenEndpoint, form, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                LogMsg($"Token refresh failed ({(int)resp.StatusCode}): {body}");

                if ((int)resp.StatusCode == 400 || (int)resp.StatusCode == 401)
                {
                    // Refresh token is dead (commonly invalid_grant from the
                    // 7-day Testing-mode expiry). Wipe it, then automatically
                    // reopen the browser to re-consent rather than waiting
                    // for the user to notice and reconnect manually.
                    Disconnect();

                    if (!_reauthAttemptedThisRun)
                    {
                        _reauthAttemptedThisRun = true;
                        LogMsg("Reconnecting to Google Health automatically (refresh token expired)...");
                        var reconnected = await ConnectAsync(ct);
                        if (reconnected) return CurrentTokens;
                    }
                    else
                    {
                        LogMsg("Already attempted an automatic reconnect this run — not opening another browser tab. Will retry at next launch.");
                    }
                }
                return null;
            }

            var parsed = ParseTokenResponse(body);
            if (parsed == null) return null;

            // Refresh responses commonly omit refresh_token — keep the
            // existing one when Google doesn't issue a new one.
            if (string.IsNullOrEmpty(parsed.RefreshToken))
                parsed.RefreshToken = current.RefreshToken;

            _storage.SaveTokens(parsed);
            _cachedTokens = parsed;
            return parsed;
        }
        catch (Exception ex)
        {
            LogMsg($"Token refresh error: {ex.Message}");
            return null;
        }
    }

    private static GoogleHealthTokens? ParseTokenResponse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var tokens = new GoogleHealthTokens
            {
                AccessToken  = root.GetProperty("access_token").GetString() ?? "",
                ExpiresAtUtc = DateTime.UtcNow.AddSeconds(root.GetProperty("expires_in").GetDouble()),
            };
            if (root.TryGetProperty("refresh_token", out var rt))
                tokens.RefreshToken = rt.GetString() ?? "";

            return tokens;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Returns a token guaranteed to be valid (refreshing if needed), or null if connection is broken.</summary>
    private async Task<GoogleHealthTokens?> GetValidTokensAsync(CancellationToken ct)
    {
        var t = CurrentTokens;
        if (t == null) return null;
        if (!t.NeedsRefresh) return t;
        return await RefreshAsync(t, ct);
    }

    // =========================================================================
    // Activity upload
    // =========================================================================

    /// <summary>
    /// Uploads the given session to Google Health as an "exercise" data
    /// point. Treats any non-success response as recoverable (Success=false,
    /// Error set) — the caller should leave the session unuploaded for the
    /// next retry sweep.
    /// </summary>
    public async Task<GoogleHealthUploadResult> UploadAsync(
        SessionRecord     session,
        WalkActivityType  activityType = WalkActivityType.Walk,
        CancellationToken ct           = default)
    {
        var tokens = await GetValidTokensAsync(ct);
        if (tokens == null)
            return new GoogleHealthUploadResult(false, null, "Not connected to Google Health.");

        var (verb, exerciseType) = activityType switch
        {
            WalkActivityType.Run => ("run", "RUNNING"),
            WalkActivityType.Jog => ("jog", "RUNNING"),
            _                    => ("walk", "WALKING"),
        };

        var offset       = TimeZoneInfo.Local.GetUtcOffset(session.StartTime);
        var startUtc     = new DateTimeOffset(DateTime.SpecifyKind(session.StartTime, DateTimeKind.Unspecified), offset).UtcDateTime;
        var endUtc       = new DateTimeOffset(DateTime.SpecifyKind(session.EndTime,   DateTimeKind.Unspecified), offset).UtcDateTime;
        var offsetString = $"{offset.TotalSeconds:0.###}s";

        var payload = new JsonObject
        {
            ["dataSource"] = new JsonObject
            {
                ["device"] = new JsonObject
                {
                    ["manufacturer"] = "TreadmillApp",
                    ["displayName"]  = "Treadmill Tracker",
                    ["formFactor"]   = "FORM_FACTOR_UNSPECIFIED",
                },
                ["recordingMethod"] = "ACTIVELY_MEASURED",
            },
            ["exercise"] = new JsonObject
            {
                ["interval"] = new JsonObject
                {
                    ["startTime"]      = startUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    ["startUtcOffset"] = offsetString,
                    ["endTime"]        = endUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    ["endUtcOffset"]   = offsetString,
                },
                ["exerciseType"] = exerciseType,
                ["displayName"]  = $"Treadmill {verb} — {session.DistanceKm:F2} km",
                ["metricsSummary"] = new JsonObject
                {
                    ["distanceMillimeters"] = (long)(session.DistanceMeters * 1000),
                    ["steps"]               = session.Steps.ToString(),
                    ["caloriesKcal"]         = session.Calories,
                },
            },
        };

        try
        {
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Post, UploadEndpoint) { Content = content };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

            var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                int code = (int)resp.StatusCode;
                LogMsg($"Google Health upload failed ({code}): {Trim(body)}");
                return new GoogleHealthUploadResult(false, null, $"HTTP {code}: {Trim(body)}");
            }

            // The create call returns a google.longrunning.Operation, not the
            // DataPoint itself. If it carries an embedded error (possible even
            // on a 2xx HTTP status for LRO-shaped APIs), treat that as a
            // failure. Otherwise pull the id from the nested "response" object
            // (the completed DataPoint) if present, else fall back to the
            // operation's own "name".
            string? dataPointId = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.Object)
                {
                    LogMsg($"Google Health upload failed (operation error): {Trim(body)}");
                    return new GoogleHealthUploadResult(false, null, $"Operation error: {Trim(body)}");
                }

                if (root.TryGetProperty("response", out var respEl) &&
                    respEl.ValueKind == JsonValueKind.Object &&
                    respEl.TryGetProperty("name", out var respNameEl))
                    dataPointId = respNameEl.GetString();
                else if (root.TryGetProperty("name", out var nameEl))
                    dataPointId = nameEl.GetString();
            }
            catch { /* fall through to synthetic id below */ }

            dataPointId ??= $"local-{session.StartTime:O}";
            LogMsg($"Uploaded to Google Health → {dataPointId}");
            return new GoogleHealthUploadResult(true, dataPointId, null);
        }
        catch (Exception ex)
        {
            LogMsg($"Google Health upload error: {ex.Message}");
            return new GoogleHealthUploadResult(false, null, ex.Message);
        }
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private void LogMsg(string msg) => Log?.Invoke(this, $"[GoogleHealth] {msg}");

    private static string Trim(string s) => s.Length > 200 ? s[..200] + "…" : s;

    public void Dispose() => _http.Dispose();
}
