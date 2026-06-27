namespace Gdrive.Mcp;

/// <summary>
/// Runtime config, env-driven. Auth uses a Google OAuth 2.0 Desktop client plus a
/// Drive-scoped refresh token, both supplied as files at deploy time (paths below).
/// The refresh token is portable JSON — minted once by a human consent (see README)
/// and reusable headlessly, so there is no interactive browser flow at runtime.
/// No host, URL, or credential is baked into source: every value defaults to a
/// neutral local placeholder and is overridden by the matching environment variable.
/// </summary>
public sealed record GdriveConfig
{
    /// <summary>OAuth 2.0 Desktop client secrets JSON (gcp-oauth.keys.json). mode 0600.</summary>
    public required string OAuthClientPath { get; init; }

    /// <summary>token.json holding the Drive-scoped refresh token (+ optional client_id/secret). mode 0600.</summary>
    public required string RefreshTokenPath { get; init; }

    /// <summary>ApplicationName reported to the Drive API.</summary>
    public required string ApplicationName { get; init; }

    /// <summary>Externally-reachable base URL of THIS MCP host, used to build download_to_url links.
    /// Defaults to a neutral local URL derived from GDRIVE_MCP_DOWNLOAD_BASE_HOST (default 127.0.0.1)
    /// and the listen port. Override GDRIVE_MCP_DOWNLOAD_BASE_URL with the host/port a downloading
    /// client can actually reach.</summary>
    public required string DownloadBaseUrl { get; init; }

    /// <summary>Lifetime of a download_to_url ticket in seconds. Short by design (signed-URL style).</summary>
    public required int DownloadTtlSeconds { get; init; }

    public static GdriveConfig FromEnvironment()
    {
        var home = Environment.GetEnvironmentVariable("HOME") ?? "/home/mcp-admin";
        var credDir = Environment.GetEnvironmentVariable("GDRIVE_MCP_CRED_DIR")
            ?? Path.Combine(home, ".gdrive-mcp");
        var port = Environment.GetEnvironmentVariable("GDRIVE_MCP_PORT") ?? "9217";
        // Neutral default host for the staged-download URL. Not bound to any deployment
        // topology — set GDRIVE_MCP_DOWNLOAD_BASE_HOST (or the full _URL) at deploy time.
        var baseHost = Environment.GetEnvironmentVariable("GDRIVE_MCP_DOWNLOAD_BASE_HOST") ?? "127.0.0.1";
        return new GdriveConfig
        {
            OAuthClientPath = Environment.GetEnvironmentVariable("GDRIVE_MCP_OAUTH_CLIENT")
                ?? Path.Combine(credDir, "gcp-oauth.keys.json"),
            RefreshTokenPath = Environment.GetEnvironmentVariable("GDRIVE_MCP_TOKEN")
                ?? Path.Combine(credDir, "token.json"),
            ApplicationName = Environment.GetEnvironmentVariable("GDRIVE_MCP_APP_NAME")
                ?? "gdrive-mcp",
            DownloadBaseUrl = Environment.GetEnvironmentVariable("GDRIVE_MCP_DOWNLOAD_BASE_URL")
                ?? $"http://{baseHost}:{port}",
            DownloadTtlSeconds = int.TryParse(
                Environment.GetEnvironmentVariable("GDRIVE_MCP_DOWNLOAD_TTL_SECONDS"), out var ttl) && ttl > 0
                ? ttl
                : 600,
        };
    }
}
