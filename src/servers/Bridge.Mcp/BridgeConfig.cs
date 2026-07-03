namespace Bridge.Mcp;

/// <summary>
/// Runtime configuration for the bridge-mcp C# server.
/// All values come from environment variables via the standard
/// IConfiguration / env binding (BRIDGE_ prefix for bridge-specific vars;
/// FORGEJO_ / ZITADEL_ etc. for shared infrastructure).
///
/// Mirrors the Python Settings class byte-for-byte so deploy env-var files
/// transfer without modification.
/// </summary>
public sealed class BridgeConfig
{
    // ----- Auth (Phase 1-3 legacy bearer) ----
    /// <summary>Static bearer token for Phase 1-3 clients (BRIDGE_BEARER_TOKEN).</summary>
    public string BridgeBearerToken { get; init; } = "";

    // ----- Forgejo -----
    /// <summary>Forgejo personal access token (FORGEJO_PAT).</summary>
    public string ForgejoPatToken { get; init; } = "";

    /// <summary>Forgejo base URL without trailing slash (FORGEJO_BASE_URL).</summary>
    public string ForgejoBaseUrl { get; init; } = "https://forgejo.example.internal";

    /// <summary>Forgejo user that owns the repos (FORGEJO_USER).</summary>
    public string ForgejoUser { get; init; } = "";

    // ----- Zitadel / OIDC (Phase 4 JWT) -----
    /// <summary>OIDC issuer base URL (ZITADEL_ISSUER).</summary>
    public string ZitadelIssuer { get; init; } = "https://auth.example.internal";

    /// <summary>OIDC client_id for the claude-mcp-bridge app (ZITADEL_CLIENT_ID).</summary>
    public string ZitadelClientId { get; init; } = "";

    // ----- Public / resource identity -----
    /// <summary>Public base URL this resource server is reached at (BRIDGE_BASE_URL).</summary>
    public string BridgeBaseUrl { get; init; } = "https://bridge.example.internal";

    /// <summary>Canonical resource indicator (RFC 8707) for this server (MCP_RESOURCE_URI).</summary>
    public string McpResourceUri { get; init; } = "https://bridge.example.internal/mcp";

    // ----- Storage / caches -----
    /// <summary>Filesystem path where cloned Forgejo repos are cached (BRIDGE_REPO_CACHE).</summary>
    public string BridgeRepoCache { get; init; } = "/tmp/bridge-repos";

    /// <summary>Repo (owner/name) that audit logs are appended to (AUDIT_REPO).</summary>
    public string AuditRepo { get; init; } = "";

    // ----- Career search -----
    /// <summary>
    /// Base URL for the merged career indexer GET /search route (CAREER_SEARCH_URL).
    /// C# target: the merged Sinapsi.Indexer on :8009. Python legacy pointed at :8010.
    /// </summary>
    public string CareerSearchUrl { get; init; } = "http://indexer.internal:8009";

    /// <summary>
    /// Bearer token for the career indexer (CAREER_SEARCH_TOKEN).
    /// Empty string means the tool returns not_configured before any I/O.
    /// </summary>
    public string CareerSearchToken { get; init; } = "";

    // ----- OAuth scopes (informational — advertised in RFC 9728 metadata) -----
    /// <summary>
    /// Scopes declared in /.well-known/oauth-protected-resource.
    /// Includes bridge:read:emails even though no tool enforces it (preserve for OAuth discovery).
    /// </summary>
    public static readonly string[] ScopesSupported =
    [
        "bridge:deposit",
        "bridge:read:documents",
        "bridge:read:facts",
        "bridge:read:facts_sensitive",
        "bridge:read:emails",
        "bridge:context_pack",
    ];

    // ----- Rate limits (requests per minute) -----
    public int RateLimitDepositPerMin { get; init; } = 30;
    public int RateLimitReadPerMin { get; init; } = 60;
    public int RateLimitSensitivePerMin { get; init; } = 5;

    // ----- Misc -----
    public int ContextPackCharBudget { get; init; } = 30_000;

    public const string Version = "0.1.0";

    // ----- Derived -----
    /// <summary>Zitadel JWKS endpoint derived from the issuer.</summary>
    public string JwksUrl => ZitadelIssuer.TrimEnd('/') + "/oauth/v2/keys";

    /// <summary>Read the config from env vars (BRIDGE_ prefix for bridge-specific).</summary>
    public static BridgeConfig FromEnvironment()
    {
        static string Env(string name, string fallback) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;
        static int EnvInt(string name, int fallback) =>
            int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;

        return new BridgeConfig
        {
            BridgeBearerToken   = Env("BRIDGE_BEARER_TOKEN", ""),
            ForgejoPatToken     = Env("FORGEJO_PAT", ""),
            ForgejoBaseUrl      = Env("FORGEJO_BASE_URL", "https://forgejo.example.internal"),
            ForgejoUser         = Env("FORGEJO_USER", ""),
            ZitadelIssuer       = Env("ZITADEL_ISSUER", "https://auth.example.internal"),
            ZitadelClientId     = Env("ZITADEL_CLIENT_ID", ""),
            BridgeBaseUrl       = Env("BRIDGE_BASE_URL", "https://bridge.example.internal"),
            McpResourceUri      = Env("MCP_RESOURCE_URI", "https://bridge.example.internal/mcp"),
            BridgeRepoCache     = Env("BRIDGE_REPO_CACHE", "/tmp/bridge-repos"),
            AuditRepo           = Env("AUDIT_REPO", ""),
            CareerSearchUrl     = Env("CAREER_SEARCH_URL", "http://indexer.internal:8009"),
            CareerSearchToken   = Env("CAREER_SEARCH_TOKEN", ""),

            RateLimitDepositPerMin   = EnvInt("BRIDGE_RATE_LIMIT_DEPOSIT_PER_MIN",   30),
            RateLimitReadPerMin      = EnvInt("BRIDGE_RATE_LIMIT_READ_PER_MIN",       60),
            RateLimitSensitivePerMin = EnvInt("BRIDGE_RATE_LIMIT_SENSITIVE_PER_MIN",  5),
            ContextPackCharBudget    = EnvInt("BRIDGE_CONTEXT_PACK_CHAR_BUDGET",      30_000),
        };
    }
}
