using System;
using System.Text.Json;
using Windows.Security.Credentials;

namespace TreadmillApp.Services.Strava;

/// <summary>
/// Persists Strava credentials and OAuth tokens in Windows Credential Manager
/// (DPAPI-encrypted, scoped to the current user). Two records are stored under
/// distinct resource names so they can be wiped independently — "Disconnect"
/// removes only the tokens, "Forget Credentials" removes both.
/// </summary>
internal sealed class StravaSecureStorage
{
    private const string CredentialsResource = "TreadmillApp.Strava.ClientCredentials";
    private const string TokensResource      = "TreadmillApp.Strava.Tokens";
    private const string TokensUserName      = "tokens";

    private readonly PasswordVault _vault = new();

    // ── Client credentials ────────────────────────────────────────────────────

    public StravaCredentials? LoadCredentials()
    {
        try
        {
            var creds = _vault.FindAllByResource(CredentialsResource);
            if (creds == null || creds.Count == 0) return null;

            var first = creds[0];
            first.RetrievePassword();
            return new StravaCredentials(first.UserName, first.Password);
        }
        catch
        {
            return null;
        }
    }

    public void SaveCredentials(StravaCredentials creds)
    {
        ClearCredentials();
        _vault.Add(new PasswordCredential(CredentialsResource, creds.ClientId, creds.ClientSecret));
    }

    public void ClearCredentials()
    {
        try
        {
            var existing = _vault.FindAllByResource(CredentialsResource);
            foreach (var c in existing) _vault.Remove(c);
        }
        catch { /* none to remove */ }
    }

    // ── Tokens ────────────────────────────────────────────────────────────────

    public StravaTokens? LoadTokens()
    {
        try
        {
            var cred = _vault.Retrieve(TokensResource, TokensUserName);
            cred.RetrievePassword();
            return JsonSerializer.Deserialize<StravaTokens>(cred.Password);
        }
        catch
        {
            return null;
        }
    }

    public void SaveTokens(StravaTokens tokens)
    {
        ClearTokens();
        var json = JsonSerializer.Serialize(tokens);
        _vault.Add(new PasswordCredential(TokensResource, TokensUserName, json));
    }

    public void ClearTokens()
    {
        try
        {
            var existing = _vault.FindAllByResource(TokensResource);
            foreach (var c in existing) _vault.Remove(c);
        }
        catch { /* none to remove */ }
    }
}
