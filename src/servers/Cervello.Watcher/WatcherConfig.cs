using Npgsql;

namespace Cervello.Watcher;

/// <summary>
/// Runtime config, env-driven, fail-closed (mirrors <c>GdriveConfig</c>): a bad
/// numeric / range value throws at startup naming the offending env var, rather
/// than silently honouring a footgun. No host, path, or credential is baked into
/// source — every value defaults to a neutral local placeholder and is overridden
/// by the matching environment variable.
///
/// M6-refine DIVERGENCE (supersedes the original D1/D2 Google-SA design): Drive
/// access goes through the existing homelab <c>gdrive</c> MCP via the CT121
/// agentgateway, authenticated as a scoped agentgateway machine identity
/// (<see cref="WatcherAgent"/>, minted by Sinapsi.AgentJwt — agent-free, JWK
/// provisioned by the deploy-controller/Infisical pattern), NOT a Google service
/// account. CT146-cervello already has CT121-mcp-gateway as a fixed allowlisted
/// LAN egress peer (cervello_egress_lan_allow, ports 443/8443) — no proxy hop
/// needed for this call.
/// </summary>
public sealed record WatcherConfig
{
    /// <summary>The CT121-mcp-gateway MCP endpoint the watcher calls gdrive_* tools through.</summary>
    public required string GatewayUrl { get; init; }

    /// <summary>The watcher's scoped agentgateway machine identity (JWK file <c>&lt;AGENT_KEY_DIR&gt;/&lt;agent&gt;.json</c>).</summary>
    public required string WatcherAgent { get; init; }

    /// <summary>Poll interval (seconds) for the recordings folder listing. Default ~60 s.</summary>
    public required int PollIntervalSeconds { get; init; }

    /// <summary>Drive folder path whose changes we keep (client-side scope filter). D3.</summary>
    public required string FolderPath { get; init; }

    /// <summary>Drive folder id for FolderPath (resolved once via gdrive_search_files, or set directly). D3.</summary>
    public string? FolderId { get; init; }

    /// <summary>CT-local staging dir for downloaded blobs. MUST be under /var/lib/cervello. Custody.</summary>
    public required string StagingDir { get; init; }

    /// <summary>Npgsql DSN (connection string) for the on-CT cervello Postgres. D4.</summary>
    public required string PostgresDsn { get; init; }

    /// <summary>CT-local working tree of ste/cervello where manifest.yaml is written. D7.</summary>
    public required string RepoWorkingTree { get; init; }

    /// <summary>Bind host for the opaque health endpoint.</summary>
    public required string HealthHost { get; init; }

    /// <summary>Bind port for the opaque health endpoint (fail-closed 1..65535).</summary>
    public required int HealthPort { get; init; }

    /// <summary>Per-request ceiling (seconds) applied to the gateway HttpClient.Timeout.</summary>
    public required int HttpTimeoutSeconds { get; init; }

    /// <summary>Identity string for logs/diagnostics (was reported to the Drive API pre-M6-refine).</summary>
    public required string ApplicationName { get; init; }

    // ---- bounds (fail-closed) ----
    internal const string DefaultGatewayUrl = "http://127.0.0.1:8443/mcp";
    internal const string DefaultWatcherAgent = "agent-cervello-watcher";
    internal const string DefaultFolderPath = "cervello/recordings";
    internal const string DefaultStagingDir = "/var/lib/cervello/staging";
    internal const string DefaultRepoWorkingTree = "/var/lib/cervello/repo";

    internal const int DefaultPollIntervalSeconds = 60;
    internal const int MinPollIntervalSeconds = 5;
    internal const int MaxPollIntervalSeconds = 3_600;

    internal const int DefaultHttpTimeoutSeconds = 100;
    internal const int MaxHttpTimeoutSeconds = 3_600;

    internal const int DefaultHealthPort = 8146;

    /// <summary>Read config from the process environment (production path).</summary>
    public static WatcherConfig FromEnvironment() => From(Environment.GetEnvironmentVariable);

    /// <summary>
    /// Read config from an INJECTABLE env source (test-isolation: tests pass a LOCAL
    /// map instead of mutating the process environment, so a fail-closed bad-value
    /// test cannot leak into a parallel test). <see cref="FromEnvironment"/> supplies
    /// <c>Environment.GetEnvironmentVariable</c>.
    /// </summary>
    public static WatcherConfig From(Func<string, string?> getEnv)
    {
        string Env(string k, string dflt) =>
            getEnv(k) is { Length: > 0 } v ? v : dflt;

        return new WatcherConfig
        {
            GatewayUrl = ReadGatewayUrl(getEnv),
            WatcherAgent = Env("CERVELLO_WATCHER_AGENT", DefaultWatcherAgent),
            PollIntervalSeconds = ReadBoundedInt(getEnv,
                "CERVELLO_WATCHER_POLL_INTERVAL_SECONDS", DefaultPollIntervalSeconds,
                MinPollIntervalSeconds, MaxPollIntervalSeconds),
            FolderPath = Env("CERVELLO_WATCHER_FOLDER_PATH", DefaultFolderPath),
            FolderId = getEnv("CERVELLO_WATCHER_FOLDER_ID"),
            StagingDir = Env("CERVELLO_WATCHER_STAGING_DIR", DefaultStagingDir),
            PostgresDsn = ReadPostgresDsn(getEnv),
            RepoWorkingTree = Env("CERVELLO_WATCHER_REPO_WORKTREE", DefaultRepoWorkingTree),
            HealthHost = Env("CERVELLO_WATCHER_HEALTH_HOST", "0.0.0.0"),
            HealthPort = ReadBoundedInt(getEnv, "CERVELLO_WATCHER_HEALTH_PORT", DefaultHealthPort, 1, 65_535),
            HttpTimeoutSeconds = ReadBoundedInt(getEnv,
                "CERVELLO_WATCHER_HTTP_TIMEOUT_SECONDS", DefaultHttpTimeoutSeconds, 1, MaxHttpTimeoutSeconds),
            ApplicationName = Env("CERVELLO_WATCHER_APP_NAME", "cervello-watcher"),
        };
    }

    /// <summary>Convenience overload: read from a LOCAL dictionary (tests).</summary>
    public static WatcherConfig From(IReadOnlyDictionary<string, string?> env) =>
        From(k => env.TryGetValue(k, out var v) ? v : null);

    /// <summary>
    /// Gateway url is fail-closed: an unparseable / non-http(s) value throws naming
    /// the env var, so a typo cannot silently point the watcher at an unintended
    /// endpoint (mirrors the old proxy-url validation this replaces).
    /// </summary>
    private static string ReadGatewayUrl(Func<string, string?> getEnv)
    {
        const string envVar = "GATEWAY_URL";
        var raw = getEnv(envVar);
        if (string.IsNullOrEmpty(raw))
            return DefaultGatewayUrl;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException(
                $"{envVar}='{raw}' is invalid: expected an absolute http(s) gateway URL " +
                $"(default {DefaultGatewayUrl}).");
        return raw;
    }

    /// <summary>
    /// Build the Postgres DSN either from a full DSN env var, or from discrete
    /// CERVELLO_DB_* parts with a neutral local default (mirrors PostgresIndexStore).
    /// </summary>
    private static string ReadPostgresDsn(Func<string, string?> getEnv)
    {
        string Env(string k, string dflt) => getEnv(k) is { Length: > 0 } v ? v : dflt;

        var dsn = getEnv("CERVELLO_WATCHER_DB_DSN");
        if (!string.IsNullOrEmpty(dsn))
            return dsn;
        return new NpgsqlConnectionStringBuilder
        {
            Host = Env("CERVELLO_DB_HOST", "127.0.0.1"),
            Port = ReadBoundedInt(getEnv, "CERVELLO_DB_PORT", 5432, 1, 65_535),
            Database = Env("CERVELLO_DB_NAME", "cervello"),
            Username = Env("CERVELLO_DB_USER", "cervello"),
            Password = getEnv("CERVELLO_DB_PASSWORD") ?? "",
            SslMode = SslMode.Prefer,
            Pooling = true,
            MaxPoolSize = 10,
            Timeout = 15,
        }.ConnectionString;
    }

    /// <summary>Fail-closed bounded int: non-numeric / out-of-range throws naming the var.</summary>
    private static int ReadBoundedInt(Func<string, string?> getEnv, string envVar, int dflt, int min, int max)
    {
        var raw = getEnv(envVar);
        if (string.IsNullOrEmpty(raw))
            return dflt;
        if (!int.TryParse(raw, out var v) || v < min || v > max)
            throw new InvalidOperationException(
                $"{envVar}='{raw}' is invalid: expected an integer in {min}..{max} (default {dflt}).");
        return v;
    }
}
