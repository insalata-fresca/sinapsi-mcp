using Npgsql;

namespace Cervello.Watcher;

/// <summary>
/// Runtime config, env-driven, fail-closed (mirrors <c>GdriveConfig</c>): a bad
/// numeric / range value throws at startup naming the offending env var, rather
/// than silently honouring a footgun. No host, path, or credential is baked into
/// source — every value defaults to a neutral local placeholder and is overridden
/// by the matching environment variable.
///
/// DIVERGENCE from GdriveConfig (D1): auth is a read-only ServiceAccountCredential
/// (SA JSON key at <see cref="ServiceAccountKeyPath"/>), NOT an OAuth refresh token.
/// All Drive egress is via the CT proxy (D2, default <c>http://127.0.0.1:13130</c>).
/// </summary>
public sealed record WatcherConfig
{
    /// <summary>HTTP(S) forward proxy for ALL Drive egress (tinyproxy-cervello). D2.</summary>
    public required string HttpProxyUrl { get; init; }

    /// <summary>Poll interval (seconds) for the Changes feed. Default ~60 s.</summary>
    public required int PollIntervalSeconds { get; init; }

    /// <summary>Drive folder path whose changes we keep (client-side scope filter). D3.</summary>
    public required string FolderPath { get; init; }

    /// <summary>Path to the read-only service-account JSON key (0600, from Infisical). D1.</summary>
    public required string ServiceAccountKeyPath { get; init; }

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

    /// <summary>Per-request ceiling (seconds) applied to the Drive HttpClient.Timeout.</summary>
    public required int HttpTimeoutSeconds { get; init; }

    /// <summary>ApplicationName reported to the Drive API.</summary>
    public required string ApplicationName { get; init; }

    // ---- bounds (fail-closed) ----
    internal const string DefaultProxyUrl = "http://127.0.0.1:13130";
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
            HttpProxyUrl = ReadProxyUrl(getEnv),
            PollIntervalSeconds = ReadBoundedInt(getEnv,
                "CERVELLO_WATCHER_POLL_INTERVAL_SECONDS", DefaultPollIntervalSeconds,
                MinPollIntervalSeconds, MaxPollIntervalSeconds),
            FolderPath = Env("CERVELLO_WATCHER_FOLDER_PATH", DefaultFolderPath),
            ServiceAccountKeyPath = Env("CERVELLO_WATCHER_SA_KEY_PATH", "/run/secrets/cervello-sa.json"),
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
    /// Proxy url is fail-closed: an unparseable / non-http(s) value throws naming the
    /// env var, so a typo cannot silently disable proxying and leak direct Google egress.
    /// </summary>
    private static string ReadProxyUrl(Func<string, string?> getEnv)
    {
        const string envVar = "CERVELLO_WATCHER_HTTP_PROXY";
        var raw = getEnv(envVar);
        if (string.IsNullOrEmpty(raw))
            return DefaultProxyUrl;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException(
                $"{envVar}='{raw}' is invalid: expected an absolute http(s) proxy URL " +
                $"(default {DefaultProxyUrl}).");
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
