using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TreadmillApp.Services;

namespace TreadmillApp.Services.Strava;

/// <summary>
/// Public API to Strava: connect (one-time OAuth), upload completed walks,
/// disconnect, forget credentials. All token refresh is handled internally.
/// </summary>
public sealed class StravaService : IDisposable
{
    private const string AuthorizeEndpoint = "https://www.strava.com/oauth/authorize";
    private const string TokenEndpoint     = "https://www.strava.com/oauth/token";
    private const string UploadEndpoint    = "https://www.strava.com/api/v3/activities";
    private const string Scope             = "activity:write";

    private readonly StravaSecureStorage _storage = new();
    // Strava's /activities endpoint can be slow under load (anywhere up to a
    // minute). 90s comfortably covers the slow tail without making a stuck
    // request feel frozen forever.
    private readonly HttpClient          _http    = new() { Timeout = TimeSpan.FromSeconds(90) };

    public event EventHandler<string>? Log;

    private StravaTokens? _cachedTokens;

    public bool   HasCredentials => _storage.LoadCredentials() != null;
    public bool   IsConnected    => CurrentTokens != null;
    public string AthleteName    => CurrentTokens?.AthleteName ?? "";

    private StravaTokens? CurrentTokens => _cachedTokens ??= _storage.LoadTokens();

    public StravaCredentials? GetCredentials() => _storage.LoadCredentials();
    public void SaveCredentials(StravaCredentials creds) => _storage.SaveCredentials(creds);

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
    // OAuth — one-time authorization flow
    // =========================================================================

    /// <summary>
    /// Runs the OAuth authorization flow: spins up a localhost listener on a
    /// random ephemeral port, opens the user's browser to Strava's consent
    /// page, exchanges the returned code for tokens, and persists them.
    /// </summary>
    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        var creds = _storage.LoadCredentials();
        if (creds == null)
        {
            LogMsg("No Client ID/Secret saved. Enter them on the Strava tab first.");
            return false;
        }

        // 1. Bind a random local port for the OAuth callback
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port        = ((IPEndPoint)listener.LocalEndpoint).Port;
        var redirectUri = $"http://localhost:{port}/callback";
        var state       = Guid.NewGuid().ToString("N");

        // 2. Open the browser to the authorize URL
        var authUrl =
            $"{AuthorizeEndpoint}" +
            $"?client_id={Uri.EscapeDataString(creds.ClientId)}" +
            $"&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&scope={Uri.EscapeDataString(Scope)}" +
            $"&approval_prompt=auto" +
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

            // Send a nice page back to the browser before closing
            await WriteBrowserResponseAsync(tcp, error == null
                ? "<h2>Connected!</h2><p>You can close this tab and return to Treadmill Tracker.</p>"
                : $"<h2>Authorization failed</h2><p>{HtmlEncode(error)}</p>");

            if (error != null)        { LogMsg($"Strava authorization failed: {error}"); return false; }
            if (returnedState != state) { LogMsg("Strava authorization failed: state mismatch (possible CSRF)."); return false; }
            if (string.IsNullOrEmpty(code)) { LogMsg("Strava authorization failed: no code returned."); return false; }

            // 4. Exchange the code for tokens
            var tokens = await ExchangeCodeAsync(creds, code!, ct);
            if (tokens == null) return false;

            _storage.SaveTokens(tokens);
            _cachedTokens = tokens;
            LogMsg($"Connected to Strava as {tokens.AthleteName}.");
            return true;
        }
        catch (OperationCanceledException)
        {
            LogMsg("Strava authorization timed out (no response within 5 minutes).");
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

    private async Task<StravaTokens?> ExchangeCodeAsync(StravaCredentials creds, string code, CancellationToken ct)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"]     = creds.ClientId,
            ["client_secret"] = creds.ClientSecret,
            ["code"]          = code,
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
            return ParseTokenResponse(body);
        }
        catch (Exception ex)
        {
            LogMsg($"Token exchange error: {ex.Message}");
            return null;
        }
    }

    private async Task<StravaTokens?> RefreshAsync(StravaTokens current, CancellationToken ct)
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
                    // Refresh token is dead — wipe and force user to reconnect
                    Disconnect();
                }
                return null;
            }

            // Refresh response carries new access_token, refresh_token, expires_at
            // but does NOT include the athlete object — preserve identity from the
            // existing tokens.
            var parsed = ParseTokenResponse(body);
            if (parsed == null) return null;
            parsed.AthleteId        = current.AthleteId;
            parsed.AthleteFirstName = current.AthleteFirstName;
            parsed.AthleteLastName  = current.AthleteLastName;

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

    private static StravaTokens? ParseTokenResponse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var tokens = new StravaTokens
            {
                AccessToken  = root.GetProperty("access_token").GetString() ?? "",
                RefreshToken = root.GetProperty("refresh_token").GetString() ?? "",
                ExpiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(
                                   root.GetProperty("expires_at").GetInt64()).UtcDateTime,
            };

            if (root.TryGetProperty("athlete", out var ath) && ath.ValueKind == JsonValueKind.Object)
            {
                if (ath.TryGetProperty("id",        out var id))   tokens.AthleteId        = id.GetInt64();
                if (ath.TryGetProperty("firstname", out var fn))   tokens.AthleteFirstName = fn.GetString() ?? "";
                if (ath.TryGetProperty("lastname",  out var ln))   tokens.AthleteLastName  = ln.GetString() ?? "";
            }
            return tokens;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Returns a token guaranteed to be valid (refreshing if needed), or null if connection is broken.</summary>
    private async Task<StravaTokens?> GetValidTokensAsync(CancellationToken ct)
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
    /// Uploads the given session to Strava with the appropriate sport_type
    /// based on the classification (Walk → Walk, Jog/Run → Run).
    /// Treats network failure / 5xx as recoverable (Success=false, Error set).
    /// Treats 401/refresh failure as non-recoverable for this attempt
    /// (caller should leave the session unuploaded for next-startup retry).
    /// </summary>
    public async Task<StravaUploadResult> UploadAsync(
        SessionRecord     session,
        WalkActivityType  activityType = WalkActivityType.Walk,
        CancellationToken ct           = default)
    {
        var tokens = await GetValidTokensAsync(ct);
        if (tokens == null)
            return new StravaUploadResult(false, null, null, "Not connected to Strava.");

        // Walk → Walk; Jog and Run both → Run on Strava (Strava has no separate
        // "jog" sport_type, but the activity name preserves the distinction).
        var (verb, sportType) = activityType switch
        {
            WalkActivityType.Run => ("run", "Run"),
            WalkActivityType.Jog => ("jog", "Run"),
            _                    => ("walk", "Walk"),
        };

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["name"]              = $"Treadmill {verb} — {session.DistanceKm:F2} km",
            ["type"]              = sportType,
            ["sport_type"]        = sportType,
            ["start_date_local"]  = session.StartTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["elapsed_time"]      = ((int)session.Duration.TotalSeconds).ToString(),
            ["distance"]          = session.DistanceMeters.ToString(),
            ["trainer"]           = "1",
            ["description"]       = $"Logged by Treadmill Tracker · {session.Steps} steps · {session.Calories} kcal",
        });

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, UploadEndpoint) { Content = form };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.AccessToken);

            var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                int code = (int)resp.StatusCode;

                // 409 = duplicate activity (Strava de-dup window matched). Mark as
                // resolved locally so the retry sweep doesn't keep hitting it.
                if (code == 409)
                {
                    LogMsg($"Strava reports activity for {session.StartTime:yyyy-MM-dd HH:mm} already exists (409). Marking as uploaded locally.");
                    return new StravaUploadResult(false, null, null, "duplicate", Permanent: true);
                }

                // 401 after a refresh failure / revoked grant — caller should stop.
                bool permanent = code == 401;
                LogMsg($"Strava upload failed ({code}): {Trim(body)}");
                return new StravaUploadResult(false, null, null, $"HTTP {code}: {Trim(body)}", Permanent: permanent);
            }

            using var doc = JsonDocument.Parse(body);
            var id  = doc.RootElement.GetProperty("id").GetInt64();
            var url = $"https://www.strava.com/activities/{id}";
            LogMsg($"Uploaded to Strava → {url}");
            return new StravaUploadResult(true, id, url, null);
        }
        catch (Exception ex)
        {
            LogMsg($"Strava upload error: {ex.Message}");
            return new StravaUploadResult(false, null, null, ex.Message);
        }
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private void LogMsg(string msg) => Log?.Invoke(this, $"[Strava] {msg}");

    private static string Trim(string s) => s.Length > 200 ? s[..200] + "…" : s;

    public void Dispose() => _http.Dispose();
}
