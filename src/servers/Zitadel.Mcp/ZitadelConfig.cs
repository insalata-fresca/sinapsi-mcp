namespace Zitadel.Mcp;

/// <summary>How the host authenticates to the ZITADEL management API.</summary>
public enum ZitadelAuthMode
{
    /// <summary>Static bearer token (PAT / long-lived service-account token) held server-side.</summary>
    Pat,

    /// <summary>Service-account JSON key: the host self-mints + auto-refreshes a short-lived JWT
    /// bearer via ZITADEL's RFC 7523 <c>jwt-bearer</c> grant. No long-lived credential is held.</summary>
    ServiceAccountKey,
}

/// <summary>
/// Runtime configuration for the Zitadel.Mcp host. The instance is selected entirely by
/// environment, so a host pointed at any ZITADEL deployment is the same binary configured
/// differently — never forked code.
///
/// <para>
/// Two auth modes are supported, selected fail-closed by which env vars are present:
/// </para>
/// <list type="number">
///   <item>
///     <b>Service-account-key mode (preferred).</b> If <c>ZITADEL_SA_KEY_FILE</c> is set, the host
///     loads a ZITADEL service-account JSON key and self-mints a short-lived JWT bearer (RFC 7523
///     <c>jwt-bearer</c> grant), auto-refreshing it. No long-lived credential is held. This matches
///     the stronger, canonical deploy model — the same binary needs no static token.
///   </item>
///   <item>
///     <b>PAT mode.</b> Otherwise, if <c>ZITADEL_TOKEN</c> is set, the host attaches that static
///     bearer to every call (unchanged legacy behaviour).
///   </item>
/// </list>
///
/// <para>Env vars:</para>
/// <list type="bullet">
///   <item><c>ZITADEL_BASE_URL</c> / <c>ZITADEL_API_URL</c> — the ZITADEL API root the host calls,
///     e.g. <c>https://auth.example.com</c> (the <c>/management/v1/</c> and <c>/admin/v1/</c> API
///     paths are appended per call). <c>ZITADEL_API_URL</c> is an alias the SA-key deploy provides
///     (it may be a LAN-bypass origin, e.g. <c>http://10.x.y.z:80</c>); either is accepted.</item>
///   <item><c>ZITADEL_ISSUER</c> — the ZITADEL public issuer (the <c>aud</c> of the minted JWT
///     assertion + the value ZITADEL validates the token against). Defaults to <c>ZITADEL_BASE_URL</c>
///     / <c>ZITADEL_API_URL</c> when unset. SA-key mode only.</item>
///   <item><c>ZITADEL_SA_KEY_FILE</c> — path to the service-account JSON key (<c>{type, keyId, key,
///     userId}</c>). Its presence selects SA-key mode.</item>
///   <item><c>ZITADEL_HOST_HEADER</c> — the <c>Host</c> header sent on API calls (+ the JWT-mint
///     call). Needed when the API root is a LAN-bypass IP but ZITADEL validates the issuer against
///     the host header. Defaults to the API-root host. SA-key mode only.</item>
///   <item><c>ZITADEL_TOKEN</c> — a static service-account / PAT bearer token, held server-side,
///     injected at deploy — never baked in. Selects PAT mode when no SA key file is set.</item>
///   <item><c>ZITADEL_MCP_PORT</c> — listen port. A non-numeric / out-of-range value FAILS
///     startup.</item>
///   <item><c>ZITADEL_HTTP_TIMEOUT_MS</c> — hard ceiling on a single upstream HTTP call. A
///     non-numeric, <c>&lt;= 0</c>, or out-of-range value FAILS startup.</item>
///   <item><c>AGENT_KEY_DIR</c> — host-side directory the create_machine_key tool writes a machine
///     user's JSON private key into (mode 0640); the key is NEVER returned to the caller. Defaults
///     to <c>/agent-keys</c>.</item>
/// </list>
/// </summary>
public sealed record ZitadelConfig(
    string BaseUrl,
    ZitadelAuthMode AuthMode,
    string? Token,
    string? SaKeyFile,
    string Issuer,
    string HostHeader,
    int Port,
    string AgentKeyDir,
    int HttpTimeoutMs)
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
        // API root: ZITADEL_BASE_URL (legacy) or ZITADEL_API_URL (the alias the SA-key deploy
        // provides — may be a LAN-bypass origin). Either is accepted; neither → fail closed.
        var baseUrl = (Env("ZITADEL_BASE_URL") ?? Env("ZITADEL_API_URL") ?? throw new InvalidOperationException(
            "ZITADEL_BASE_URL (or ZITADEL_API_URL) not set (e.g. https://auth.example.com)."))
            .TrimEnd('/');

        var saKeyFile = Env("ZITADEL_SA_KEY_FILE");
        var token = Env("ZITADEL_TOKEN");

        // Fail-closed auth-mode selection. SA-key mode is preferred when a key file is configured;
        // otherwise a static token is required. If neither is fully configured we throw naming both
        // paths, so a misconfigured deploy fails at startup rather than silently running with no auth.
        ZitadelAuthMode mode;
        if (saKeyFile is not null)
        {
            mode = ZitadelAuthMode.ServiceAccountKey;
        }
        else if (token is not null)
        {
            mode = ZitadelAuthMode.Pat;
        }
        else
        {
            throw new InvalidOperationException(
                "No ZITADEL auth configured. Set ZITADEL_SA_KEY_FILE (preferred: a service-account " +
                "JSON key the host mints a short-lived JWT from) or ZITADEL_TOKEN (a static " +
                "service-account/PAT bearer). Both must be injected at deploy, never baked in.");
        }

        // Issuer: the aud of the minted JWT + the value ZITADEL validates the token against.
        // Defaults to the API root when unset (the common case where the API root IS the issuer).
        var issuer = (Env("ZITADEL_ISSUER") ?? baseUrl).TrimEnd('/');

        // Host header: sent on API + mint calls so ZITADEL sees the public host even when the API
        // root is a LAN-bypass IP. Defaults to the API-root host.
        var hostHeader = Env("ZITADEL_HOST_HEADER") ?? new Uri(baseUrl).Host;

        var port = ReadPort();
        var agentKeyDir = Env("AGENT_KEY_DIR") ?? DefaultAgentKeyDir;
        var httpTimeoutMs = ReadHttpTimeoutMs();
        return new ZitadelConfig(baseUrl, mode, token, saKeyFile, issuer, hostHeader, port, agentKeyDir, httpTimeoutMs);
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
