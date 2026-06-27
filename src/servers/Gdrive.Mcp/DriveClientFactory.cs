using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Services;

namespace Gdrive.Mcp;

/// <summary>
/// Builds a single authenticated <see cref="DriveService"/> for the process from a stored
/// **refresh token** (no interactive browser flow at runtime — headless-container friendly).
/// Registered as a DI singleton; the credential auto-refreshes its access token.
///
/// Reads client_id + client_secret from an OAuth Desktop client (<c>gcp-oauth.keys.json</c>)
/// and a Drive-scoped refresh token from <c>token.json</c>. The token is minted once by a
/// human consent (README) — any standard OAuth tool works, since we only need the
/// refresh_token string.
///
/// Scope at mint time must be full <see cref="DriveService.Scope.Drive"/> — required for
/// update/delete of files the app did not create (drive.file is insufficient).
/// </summary>
public sealed class DriveClientFactory
{
    public static async Task<DriveService> CreateAsync(GdriveConfig cfg, CancellationToken ct)
    {
        await using var secretsStream = File.OpenRead(cfg.OAuthClientPath);
        var secrets = GoogleClientSecrets.FromStream(secretsStream).Secrets;

        var refreshToken = ReadRefreshToken(cfg.RefreshTokenPath);
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new InvalidOperationException(
                $"no refresh_token in {cfg.RefreshTokenPath} — run the one-time Drive consent (see README).");

        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = secrets,
            Scopes = new[] { DriveService.Scope.Drive },
        });

        var credential = new UserCredential(flow, "user", new TokenResponse { RefreshToken = refreshToken });
        // Validate the refresh token works (and prime an access token) — fail fast at startup.
        await credential.RefreshTokenAsync(ct);

        return new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = cfg.ApplicationName,
        });
    }

    /// <summary>Accept either a bare refresh-token string, or a JSON object with a
    /// <c>refresh_token</c> field (the common shape emitted by google-auth tooling).</summary>
    private static string? ReadRefreshToken(string path)
    {
        var raw = File.ReadAllText(path).Trim();
        if (!raw.StartsWith('{')) return raw; // bare token

        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
    }
}
