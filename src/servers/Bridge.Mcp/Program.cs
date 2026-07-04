using Bridge.Mcp;
using Bridge.Mcp.Audit;
using Bridge.Mcp.Auth;
using Bridge.Mcp.Git;
using Bridge.Mcp.Tools;
using Sinapsi.Forge;
using Sinapsi.Forge.Gitea;
using Sinapsi.Mcp;

// ── Load config from environment ─────────────────────────────────────────────
var cfg = BridgeConfig.FromEnvironment();

// ── Build the host ────────────────────────────────────────────────────────────
var builder = WebApplication.CreateBuilder(args);

// Config singleton.
builder.Services.AddSingleton(cfg);

// Rate limiter singleton.
builder.Services.AddSingleton<BridgeRateLimiter>();

// Auth service (depends on config + limiter).
builder.Services.AddSingleton<AuthService>();

// Forgejo HTTP client + GiteaForgeClient.
var forgeBaseUrl = cfg.ForgejoBaseUrl.TrimEnd('/') + "/api/v1/";
builder.Services.AddHttpClient<IForgeClient, GiteaForgeClient>(http =>
{
    http.BaseAddress = new Uri(forgeBaseUrl);
    http.DefaultRequestHeaders.Add("Authorization", $"token {cfg.ForgejoPatToken}");
    http.Timeout = TimeSpan.FromSeconds(30);
});

// Git ops service (uses IForgeClient for REST + local git for grep).
builder.Services.AddSingleton<GitOpsService>();

// Typed + pooled HttpClient for career_search (NOT new-per-call).
// 10s timeout matches Python httpx.Client(timeout=10).
builder.Services.AddHttpClient("career-search", http =>
{
    http.Timeout = TimeSpan.FromSeconds(10);
});

// Pooled named HttpClient for raw Forgejo API calls in GitOpsService (item 6 fix).
// Previously GetForgeJsonAsync / GetForgeJsonBytesAsync used `new HttpClient()` per call —
// socket-exhaustion anti-pattern. Route through the factory instead.
// Base URL + auth header match the forge typed client above.
builder.Services.AddHttpClient("forge-raw", http =>
{
    http.BaseAddress = new Uri(cfg.ForgejoBaseUrl.TrimEnd('/') + "/api/v1/");
    http.DefaultRequestHeaders.Add("Authorization", $"token {cfg.ForgejoPatToken}");
    http.Timeout = TimeSpan.FromSeconds(30);
});

// Audit service (background writer to audit repo).
builder.Services.AddSingleton<AuditService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AuditService>());

// MCP server (stateful transport — session affinity required by claude.ai).
builder
    .AddSinapsiMcpServer("bridge-mcp", BridgeConfig.Version)
    .WithHttpTransport(o => o.Stateless = false)
    // B4b-3: Read tools (list_workspaces, list_files, read_file, check_inbox).
    .WithTools<BridgeReadTools>()
    // B4b-4: Grep tools (search_documents, lookup_fact).
    .WithTools<BridgeGrepTools>()
    // B4b-7: Context pack (get_context_pack — depends on B4b-4 grep engine).
    .WithTools<BridgeContextPackTools>()
    // B4b-5: Deposit tools (deposit_session_summary, deposit_document, deposit_artifact).
    .WithTools<BridgeDepositTools>()
    // B4b-6: Stateful tools (list_recent_additions, mark_inbox_read).
    .WithTools<BridgeStatefulTools>()
    // B4b-8: Career search (intentionally-changed tool — new indexer contract).
    .WithTools<BridgeCareerSearchTools>();

var app = builder.Build();

// Wire audit service to git ops (avoid circular DI).
var auditSvc = app.Services.GetRequiredService<AuditService>();
var gitSvc   = app.Services.GetRequiredService<GitOpsService>();
auditSvc.SetGitOps(gitSvc);

// Auth middleware — runs before every request (including /mcp).
app.UseMiddleware<BridgeAuthMiddleware>();

// /health — liveness probe (unauthenticated). Returns {status, version}.
// Note: Sinapsi.Indexer exposes its health at '/' with a different shape {status, service,
// nats_ready, ...}; no sibling MCP server has a /health route. This is a Bridge-specific
// liveness convention, not a copy of the Indexer endpoint.
app.MapGet("/health", () => Results.Json(new { status = "ok", version = BridgeConfig.Version }));

// RFC 9728 — OAuth Protected Resource metadata.
// Advertises ZITADEL_ISSUER + scopes_supported (incl. bridge:read:emails even
// though no tool enforces it — preserve for OAuth discovery parity with Python).
app.MapGet("/.well-known/oauth-protected-resource", (BridgeConfig c) =>
{
    var body = new
    {
        resource                              = c.McpResourceUri,
        authorization_servers                 = new[] { c.ZitadelIssuer.TrimEnd('/') },
        scopes_supported                      = BridgeConfig.ScopesSupported,
        bearer_methods_supported              = new[] { "header" },
        resource_documentation                = c.BridgeBaseUrl.TrimEnd('/') + "/",
        resource_signing_alg_values_supported = new[] { "RS256", "ES256" },
    };
    return Results.Json(body);
});

// Map /mcp and bind the listen address from BRIDGE_HOST / BRIDGE_PORT.
app.MapSinapsiMcp(envPrefix: "BRIDGE", defaultPort: 8000).Run();
