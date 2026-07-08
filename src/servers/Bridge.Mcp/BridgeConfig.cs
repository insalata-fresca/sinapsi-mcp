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

    // ----- Cervello open-points (S50 L3 exposure) -----
    /// <summary>
    /// Base URL for the token-gated cervello open-points HTTP surface on CT146-cervello
    /// (CERVELLO_OPEN_POINTS_URL). C# target: the enrichment host on :8147. The bridge calls
    /// GET {url}/open-points and POST {url}/open-points/{id}/answer.
    /// </summary>
    public string CervelloOpenPointsUrl { get; init; } = "http://cervello.internal:8147";

    /// <summary>
    /// Bearer token for the cervello open-points surface (CERVELLO_OPEN_POINTS_TOKEN) — the SAME
    /// operator bearer the CT146 TokenOpenPointsAuthGate expects. Empty string means the tools
    /// return not_configured before any I/O.
    /// </summary>
    public string CervelloOpenPointsToken { get; init; } = "";

    /// <summary>
    /// Emergency-disable master switch for the cervello Surface A tools (ACCESS.md §7).
    /// CERVELLO_EXPOSED=false (default true) immediately severs the open-points tools at the bridge
    /// edge — they return {status:"disabled"} before any I/O — without a redeploy.
    /// Also severs the dialogue-interaction tools (§5) — the same master switch.
    /// </summary>
    public bool CervelloExposed { get; init; } = true;

    // ----- Cervello dialogue-interaction (CB-BRIDGE — design §5) -----
    // The 7 net-new dialogue tools forward to the CT146 dialogue backend (CB-BACKEND, PR #73):
    //   read    → POST /context-pack, GET /object, GET /timeline, GET /search
    //   deposit → POST /capture, POST /goal, POST /goal/{slug}/evidence
    // The pack/object/timeline/capture/goal routes are on the enrichment host :8147 (same as
    // open-points); /search routes to the indexer :8009. All plain-HTTP behind the CT121 forwarder.

    /// <summary>
    /// Base URL for the CT146 dialogue-pack / graph-read / capture / goal routes
    /// (CERVELLO_PACK_URL). Serves POST /context-pack, GET /object, GET /timeline, POST /capture,
    /// POST /goal, POST /goal/{slug}/evidence. Default = the enrichment host on :8147 (routed via
    /// the CT121 forwarder in prod). Reuses the open-points bearer unless CERVELLO_PACK_TOKEN is set.
    /// </summary>
    public string CervelloPackUrl { get; init; } = "http://cervello.internal:8147";

    /// <summary>
    /// Base URL for the CT146 capture / goal deposit routes (CERVELLO_CAPTURE_URL). Serves
    /// POST /capture, POST /goal, POST /goal/{slug}/evidence. Default = the enrichment host on :8147
    /// (same host as pack; a distinct env var per design §5.9 so it can be split later). Reuses the
    /// pack bearer (<see cref="EffectiveCervelloPackToken"/>).
    /// </summary>
    public string CervelloCaptureUrl { get; init; } = "http://cervello.internal:8147";

    /// <summary>
    /// Bearer for the CT146 dialogue-pack/capture/goal surface (CERVELLO_PACK_TOKEN). When unset it
    /// FALLS BACK to <see cref="CervelloOpenPointsToken"/> (the design §5.9 "reuse
    /// CERVELLO_OPEN_POINTS_TOKEN" default). Effective-empty → the tools return not_configured.
    /// </summary>
    public string CervelloPackToken { get; init; } = "";

    /// <summary>
    /// Base URL for the cervello hybrid-search route (CERVELLO_SEARCH_URL) → indexer :8009,
    /// GET /search?q=&amp;kind=&amp;limit=. Default = the indexer on :8009 (routed via the CT121
    /// forwarder's reserved /search location in prod). Distinct from CAREER_SEARCH_URL: a
    /// cervello-scoped surface, not the general documents corpus.
    /// </summary>
    public string CervelloSearchUrl { get; init; } = "http://cervello.internal:8009";

    /// <summary>
    /// Bearer for the cervello indexer /search route (CERVELLO_SEARCH_TOKEN == the indexer's
    /// INDEXER_SEARCH_TOKEN, design §5.2). Empty → cervello_search returns not_configured.
    /// </summary>
    public string CervelloSearchToken { get; init; } = "";

    /// <summary>
    /// Default char budget for cervello_context_pack when the caller omits `budget`
    /// (CERVELLO_PACK_BUDGET_DEFAULT). The CT146 assembler is the authority; this is the value the
    /// bridge forwards when unspecified. Fail-closed default 30000 (matches CB-BACKEND).
    /// </summary>
    public int CervelloPackBudgetDefault { get; init; } = 30_000;

    /// <summary>Effective bearer for the pack/capture/goal surface: CERVELLO_PACK_TOKEN, else the
    /// open-points bearer (design §5.9 reuse). Empty → not_configured before any I/O.</summary>
    public string EffectiveCervelloPackToken =>
        !string.IsNullOrEmpty(CervelloPackToken) ? CervelloPackToken : CervelloOpenPointsToken;

    // ----- OAuth scopes (informational — advertised in RFC 9728 metadata) -----
    /// <summary>
    /// Scopes declared in /.well-known/oauth-protected-resource.
    /// Includes bridge:read:emails even though no tool enforces it (preserve for OAuth discovery).
    /// The bridge:cervello:* scopes gate the Surface A cervello tools (ACCESS.md §2 — a
    /// cervello-scoped credential distinct from the shared bridge token; read vs deposit split so a
    /// read-only Project binding is possible).
    /// </summary>
    public static readonly string[] ScopesSupported =
    [
        "bridge:deposit",
        "bridge:read:documents",
        "bridge:read:facts",
        "bridge:read:facts_sensitive",
        "bridge:read:emails",
        "bridge:context_pack",
        "bridge:cervello:read",
        "bridge:cervello:deposit",
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
        // CERVELLO_EXPOSED defaults TRUE; only an explicit false/0/no disables (fail-OPEN on a typo
        // is acceptable here because the CT146 token gate is the real fail-closed control).
        static bool EnvBool(string name, bool fallback) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } v
                ? v.Trim().ToLowerInvariant() is not ("false" or "0" or "no" or "off")
                : fallback;

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
            CervelloOpenPointsUrl   = Env("CERVELLO_OPEN_POINTS_URL", "http://cervello.internal:8147"),
            CervelloOpenPointsToken = Env("CERVELLO_OPEN_POINTS_TOKEN", ""),
            CervelloExposed         = EnvBool("CERVELLO_EXPOSED", true),
            // Cervello dialogue-interaction (CB-BRIDGE §5). Pack/capture/goal share the enrichment
            // host :8147; search hits the indexer :8009. Tokens: pack falls back to the open-points
            // bearer when CERVELLO_PACK_TOKEN is unset (§5.9); search reuses INDEXER_SEARCH_TOKEN.
            CervelloPackUrl          = Env("CERVELLO_PACK_URL", "http://cervello.internal:8147"),
            CervelloCaptureUrl       = Env("CERVELLO_CAPTURE_URL", "http://cervello.internal:8147"),
            CervelloPackToken        = Env("CERVELLO_PACK_TOKEN", ""),
            CervelloSearchUrl        = Env("CERVELLO_SEARCH_URL", "http://cervello.internal:8009"),
            CervelloSearchToken      = Env("CERVELLO_SEARCH_TOKEN", ""),
            CervelloPackBudgetDefault = EnvInt("CERVELLO_PACK_BUDGET_DEFAULT", 30_000),

            RateLimitDepositPerMin   = EnvInt("BRIDGE_RATE_LIMIT_DEPOSIT_PER_MIN",   30),
            RateLimitReadPerMin      = EnvInt("BRIDGE_RATE_LIMIT_READ_PER_MIN",       60),
            RateLimitSensitivePerMin = EnvInt("BRIDGE_RATE_LIMIT_SENSITIVE_PER_MIN",  5),
            ContextPackCharBudget    = EnvInt("BRIDGE_CONTEXT_PACK_CHAR_BUDGET",      30_000),
        };
    }
}
