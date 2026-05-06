using System;

namespace TreadmillApp.Services.Strava;

/// <summary>The user's Strava API application credentials (Client ID + Secret).</summary>
public sealed record StravaCredentials(string ClientId, string ClientSecret);

/// <summary>OAuth access + refresh token pair plus the basic athlete identity returned alongside.</summary>
public sealed class StravaTokens
{
    public string   AccessToken      { get; set; } = "";
    public string   RefreshToken     { get; set; } = "";
    public DateTime ExpiresAtUtc     { get; set; } = DateTime.MinValue;
    public long     AthleteId        { get; set; }
    public string   AthleteFirstName { get; set; } = "";
    public string   AthleteLastName  { get; set; } = "";

    public string AthleteName => $"{AthleteFirstName} {AthleteLastName}".Trim();

    /// <summary>True when the access token is expired (or within 60s of expiring).</summary>
    public bool NeedsRefresh => DateTime.UtcNow >= ExpiresAtUtc - TimeSpan.FromSeconds(60);
}

/// <summary>Outcome of an upload attempt.</summary>
public sealed record StravaUploadResult(
    bool    Success,
    long?   ActivityId,
    string? ActivityUrl,
    string? Error,
    bool    Permanent = false);
//      Permanent = true means "don't retry this session" (e.g. Strava already
//      has it as a duplicate, or auth has been revoked). The session should be
//      marked locally so the retry sweep stops banging on it.
