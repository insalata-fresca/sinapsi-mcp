namespace Metabase.Mcp;

// -----------------------------------------------------------------------------
// Env-driven configuration for the Metabase MCP host. Plain-ASCII banner so this
// file always diffs as TEXT, never binary.
// -----------------------------------------------------------------------------

/// <summary>
/// Runtime configuration for the Metabase.Mcp host. The instance is selected entirely by
/// environment, so a host pointed at any Metabase deployment is the same binary configured
/// differently — never forked code:
///   METABASE_BASE_URL        the Metabase root, e.g. https://metrics.example.com (the /api/
///                            paths are appended per call). Server FAILS to start if unset.
///   METABASE_API_KEY         a Metabase API key, held server-side, sent as the X-API-KEY
///                            header, injected at deploy — never baked in. FAILS to start if unset.
///   METABASE_MCP_PORT        listen port (also overridable via the Sinapsi MapSinapsiMcp
///                            default). A non-numeric / out-of-range value FAILS startup rather
///                            than being silently ignored.
///   METABASE_HTTP_TIMEOUT_MS hard ceiling on a single upstream HTTP call. A non-numeric,
///                            &lt;= 0, or out-of-range value FAILS startup.
/// </summary>
public sealed record MetabaseConfig(string BaseUrl, string ApiKey, int Port, int HttpTimeoutMs)
{
    public const int DefaultPort = 9221;

    /// <summary>Default per-request HTTP timeout (ms) when none is configured.</summary>
    public const int DefaultHttpTimeoutMs = 30_000;

    /// <summary>Upper bound on a configurable HTTP timeout (ms). 10 minutes is far past any
    /// legitimate Metabase call (including a heavy native query); a larger value is treated as a
    /// config error, not honoured.</summary>
    public const int MaxHttpTimeoutMs = 600_000;

    public static MetabaseConfig FromEnv()
    {
        var baseUrl = (Env("METABASE_BASE_URL") ?? throw new InvalidOperationException(
            "METABASE_BASE_URL not set (e.g. https://metrics.example.com)."))
            .TrimEnd('/');
        var apiKey = Env("METABASE_API_KEY") ?? throw new InvalidOperationException(
            "METABASE_API_KEY not set — the Metabase API key must be injected at deploy, not baked in.");
        var port = ReadPort();
        var httpTimeoutMs = ReadHttpTimeoutMs();
        return new MetabaseConfig(baseUrl, apiKey, port, httpTimeoutMs);
    }

    /// <summary>
    /// Read the listen port fail-closed. Previously a non-numeric value was silently swallowed
    /// by <c>int.TryParse</c> and the default was used — masking a typo'd port and letting the
    /// server bind somewhere unintended. Now a present-but-invalid value throws naming the var.
    /// </summary>
    private static int ReadPort()
    {
        var raw = Env("METABASE_MCP_PORT");
        if (raw is null)
            return DefaultPort;
        if (!int.TryParse(raw, out var p) || p is < 1 or > 65_535)
            throw new InvalidOperationException(
                $"METABASE_MCP_PORT='{raw}' is invalid: expected an integer in 1..65535 (default {DefaultPort}).");
        return p;
    }

    /// <summary>
    /// Read the per-request HTTP timeout fail-closed. A value of <c>0</c> would make every
    /// request time out instantly and a negative value throws inside the <c>HttpClient.Timeout</c>
    /// setter; any value <c>&lt;= 0</c> or above <see cref="MaxHttpTimeoutMs"/> is rejected as
    /// invalid config with a clear error naming the offending var rather than being silently
    /// honoured.
    /// </summary>
    private static int ReadHttpTimeoutMs()
    {
        var raw = Env("METABASE_HTTP_TIMEOUT_MS");
        if (raw is null)
            return DefaultHttpTimeoutMs;
        if (!int.TryParse(raw, out var ms) || ms <= 0 || ms > MaxHttpTimeoutMs)
            throw new InvalidOperationException(
                $"METABASE_HTTP_TIMEOUT_MS='{raw}' is invalid: expected an integer in 1..{MaxHttpTimeoutMs} ms " +
                $"(default {DefaultHttpTimeoutMs}).");
        return ms;
    }

    private static string? Env(string k)
    {
        var v = Environment.GetEnvironmentVariable(k);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
}
