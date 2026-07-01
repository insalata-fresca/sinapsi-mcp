namespace Zitadel.Mcp;

/// <summary>
/// Runtime configuration for the Zitadel.Mcp host. The instance is selected entirely by
/// environment, so a host pointed at any ZITADEL deployment is the same binary configured
/// differently — never forked code:
///   ZITADEL_BASE_URL         the ZITADEL instance root, e.g. https://auth.example.com (the
///                            /management/v1/ and /admin/v1/ API paths are appended per call)
///   ZITADEL_TOKEN            a service-account / PAT bearer token, held server-side, injected
///                            at deploy — never baked in
///   ZITADEL_MCP_PORT         listen port (also overridable via the Sinapsi MapSinapsiMcp
///                            default). A non-numeric / out-of-range value FAILS startup rather
///                            than being silently ignored.
///   ZITADEL_HTTP_TIMEOUT_MS  hard ceiling on a single upstream HTTP call. A non-numeric,
///                            &lt;= 0, or out-of-range value FAILS startup.
///   AGENT_KEY_DIR            host-side directory the create_machine_key tool writes a machine
///                            user's JSON private key into (mode 0640); the key is NEVER
///                            returned to the caller. Defaults to /agent-keys.
/// </summary>
public sealed record ZitadelConfig(string BaseUrl, string Token, int Port, string AgentKeyDir, int HttpTimeoutMs)
{
    public const int DefaultPort = 9220;
    public const string DefaultAgentKeyDir = "/agent-keys";

    /// <summary>Default per-request HTTP timeout (ms) when none is configured.</summary>
    public const int DefaultHttpTimeoutMs = 30_000;

    /// <summary>Upper bound on a configurable HTTP timeout (ms). 10 minutes is far past any
    /// legitimate ZITADEL management call; a larger value is treated as a config error, not
    /// honoured.</summary>
    public const int MaxHttpTimeoutMs = 600_000;

    public static ZitadelConfig FromEnv()
    {
        var baseUrl = (Env("ZITADEL_BASE_URL") ?? throw new InvalidOperationException(
            "ZITADEL_BASE_URL not set (e.g. https://auth.example.com)."))
            .TrimEnd('/');
        var token = Env("ZITADEL_TOKEN") ?? throw new InvalidOperationException(
            "ZITADEL_TOKEN not set — the ZITADEL service-account/PAT bearer token must be injected at deploy, not baked in.");
        var port = ReadPort();
        var agentKeyDir = Env("AGENT_KEY_DIR") ?? DefaultAgentKeyDir;
        var httpTimeoutMs = ReadHttpTimeoutMs();
        return new ZitadelConfig(baseUrl, token, port, agentKeyDir, httpTimeoutMs);
    }

    /// <summary>
    /// Read the listen port fail-closed. Previously a non-numeric value was silently swallowed
    /// by <c>int.TryParse</c> and the default was used — masking a typo'd port and letting the
    /// server bind somewhere unintended. Now a present-but-invalid value throws naming the var.
    /// </summary>
    private static int ReadPort()
    {
        var raw = Env("ZITADEL_MCP_PORT");
        if (raw is null)
            return DefaultPort;
        if (!int.TryParse(raw, out var p) || p is < 1 or > 65_535)
            throw new InvalidOperationException(
                $"ZITADEL_MCP_PORT='{raw}' is invalid: expected an integer in 1..65535 (default {DefaultPort}).");
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
        var raw = Env("ZITADEL_HTTP_TIMEOUT_MS");
        if (raw is null)
            return DefaultHttpTimeoutMs;
        if (!int.TryParse(raw, out var ms) || ms <= 0 || ms > MaxHttpTimeoutMs)
            throw new InvalidOperationException(
                $"ZITADEL_HTTP_TIMEOUT_MS='{raw}' is invalid: expected an integer in 1..{MaxHttpTimeoutMs} ms " +
                $"(default {DefaultHttpTimeoutMs}).");
        return ms;
    }

    private static string? Env(string k)
    {
        var v = Environment.GetEnvironmentVariable(k);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
}
