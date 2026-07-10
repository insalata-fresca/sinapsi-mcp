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

    /// <summary>Per-request ceiling (seconds) applied to the Drive
    /// <c>HttpClient.Timeout</c>, so a hung Google API call cannot pin a request
    /// forever. Bound by a canonical env var with a sane default AND a hard
    /// ceiling; a non-numeric / non-positive / out-of-range value fails startup.</summary>
    public required int HttpTimeoutSeconds { get; init; }

    /// <summary>Default Drive <c>HttpClient</c> timeout (seconds) when none is
    /// configured. Generous enough for a large-file range download, short enough
    /// to bound a hang.</summary>
    internal const int DefaultHttpTimeoutSeconds = 100;

    /// <summary>Upper bound on a configurable Drive <c>HttpClient</c> timeout
    /// (seconds). 1 hour is far past any legitimate Drive call; a larger value is
    /// treated as a config error, not honoured.</summary>
    internal const int MaxHttpTimeoutSeconds = 3_600;

    /// <summary>Whole-operation ceiling (seconds) for <c>upload_from_url</c>: the
    /// server-side fetch + streamed Drive upload is bounded by a single linked
    /// CTS, so a slow/hung source cannot pin the request forever. Larger default
    /// than the per-request Drive timeout because a multi-hundred-MB upload is a
    /// legitimately long single operation.</summary>
    public required int UploadTimeoutSeconds { get; init; }

    /// <summary>Hard ceiling (bytes) on what <c>upload_from_url</c> will pull from
    /// a source URL into Drive — enforced both against a declared Content-Length
    /// and against the actual bytes streamed (a source that omits/lies about its
    /// length is still bounded).</summary>
    public required long UploadMaxBytes { get; init; }

    /// <summary>Default whole-operation ceiling for <c>upload_from_url</c>.</summary>
    internal const int DefaultUploadTimeoutSeconds = 600;

    /// <summary>Upper bound on a configurable <c>upload_from_url</c> ceiling. 2 h is
    /// past any legitimate single upload; larger is treated as a config error.</summary>
    internal const int MaxUploadTimeoutSeconds = 7_200;

    /// <summary>Default <c>upload_from_url</c> size cap: 2 GiB.</summary>
    internal const long DefaultUploadMaxBytes = 2L * 1024 * 1024 * 1024;

    public static GdriveConfig FromEnvironment()
    {
        var home = Environment.GetEnvironmentVariable("HOME") ?? "/home/app";
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
            HttpTimeoutSeconds = ReadHttpTimeoutSeconds(),
            UploadTimeoutSeconds = ReadUploadTimeoutSeconds(),
            UploadMaxBytes = ReadUploadMaxBytes(),
        };
    }

    /// <summary>Bounds the whole <c>upload_from_url</c> operation. Canonical name
    /// <c>GDRIVE_MCP_UPLOAD_TIMEOUT_SECONDS</c>. Fail-closed: non-numeric / &lt;=0 /
    /// above <see cref="MaxUploadTimeoutSeconds"/> throws, naming the env var.</summary>
    private static int ReadUploadTimeoutSeconds()
    {
        const string envVar = "GDRIVE_MCP_UPLOAD_TIMEOUT_SECONDS";
        var raw = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrEmpty(raw))
            return DefaultUploadTimeoutSeconds;

        if (!int.TryParse(raw, out var s) || s <= 0 || s > MaxUploadTimeoutSeconds)
            throw new InvalidOperationException(
                $"{envVar}='{raw}' is invalid: expected an integer in 1..{MaxUploadTimeoutSeconds} s " +
                $"(default {DefaultUploadTimeoutSeconds}).");

        return s;
    }

    /// <summary>Size cap for <c>upload_from_url</c>. Canonical name
    /// <c>GDRIVE_MCP_UPLOAD_MAX_BYTES</c>. Fail-closed: non-numeric / &lt;=0 throws,
    /// naming the env var.</summary>
    private static long ReadUploadMaxBytes()
    {
        const string envVar = "GDRIVE_MCP_UPLOAD_MAX_BYTES";
        var raw = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrEmpty(raw))
            return DefaultUploadMaxBytes;

        if (!long.TryParse(raw, out var n) || n <= 0)
            throw new InvalidOperationException(
                $"{envVar}='{raw}' is invalid: expected a positive integer byte count " +
                $"(default {DefaultUploadMaxBytes}).");

        return n;
    }

    /// <summary>
    /// Bounds every Drive <c>HttpClient</c> request. Canonical name is
    /// <c>GDRIVE_MCP_HTTP_TIMEOUT_SECONDS</c>.
    /// <para>
    /// Fail-closed validation: a value of <c>0</c> or negative would either make
    /// every request time out instantly or throw inside <c>HttpClient.Timeout</c>;
    /// an absurdly large value defeats the purpose of a ceiling. Any non-numeric,
    /// <c>&lt;= 0</c>, or above <see cref="MaxHttpTimeoutSeconds"/> value is
    /// rejected as invalid config — we throw a clear error naming the offending
    /// env var rather than silently honouring a footgun.
    /// </para>
    /// </summary>
    private static int ReadHttpTimeoutSeconds()
    {
        const string envVar = "GDRIVE_MCP_HTTP_TIMEOUT_SECONDS";
        var raw = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrEmpty(raw))
            return DefaultHttpTimeoutSeconds;

        if (!int.TryParse(raw, out var s) || s <= 0 || s > MaxHttpTimeoutSeconds)
            throw new InvalidOperationException(
                $"{envVar}='{raw}' is invalid: expected an integer in 1..{MaxHttpTimeoutSeconds} s " +
                $"(default {DefaultHttpTimeoutSeconds}).");

        return s;
    }
}
