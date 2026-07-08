using System;

namespace TreadmillApp.Services.GoogleHealth;

/// <summary>The user's Google Cloud OAuth client credentials (Client ID + Secret).</summary>
public sealed record GoogleHealthCredentials(string ClientId, string ClientSecret);

/// <summary>OAuth access + refresh token pair.</summary>
public sealed class GoogleHealthTokens
{
    public string   AccessToken  { get; set; } = "";
    public string   RefreshToken { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; } = DateTime.MinValue;

    /// <summary>True when the access token is expired (or within 60s of expiring).</summary>
    public bool NeedsRefresh => DateTime.UtcNow >= ExpiresAtUtc - TimeSpan.FromSeconds(60);
}

/// <summary>Outcome of an upload attempt.</summary>
public sealed record GoogleHealthUploadResult(
    bool    Success,
    string? DataPointId,
    string? Error,
    bool    Permanent = false);
//      Permanent = true means "don't retry this session" locally. Transient
//      (network/5xx) failures return Permanent=false so the retry sweep tries
//      again next launch. Auth failures that need the user to re-consent are
//      handled separately (auto-reauth), not via this flag.
