using Cervello.Watcher;
using Cervello.Watcher.Drive;
using Cervello.Watcher.Ingest;
using Cervello.Watcher.Normalize;
using Cervello.Watcher.State;
using Microsoft.Extensions.Logging;
using Sinapsi.AgentJwt;
using Sinapsi.Mcp;

// -----------------------------------------------------------------------------
// Cervello.Watcher host. Like Sinapsi.Indexer's isolated shape, this binary opens
// NO NATS connection: it references neither Sinapsi.Nats nor any NATS client
// (invariant 3 / D8). The ONLY thing that leaves the CT is an opaque health
// heartbeat; audio + recording data stay on-CT (custody).
//
// M6-refine: Drive access is the existing homelab `gdrive` MCP via the CT121
// agentgateway (GatewayMcpClient), authenticated as a scoped machine identity
// (AgentJwtMinter) — NOT a Google service account. CT121-mcp-gateway is already
// a fixed allowlisted LAN egress peer for this CT (cervello_egress_lan_allow),
// so no forward proxy is needed for this call.
// -----------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);

var cfg = WatcherConfig.FromEnvironment();
builder.Services.AddSingleton(cfg);

// --- Drive seam (gdrive MCP via the agentgateway, M6-refine) ---
builder.Services.AddSingleton(AgentJwtOptions.FromEnvironment());
builder.Services.AddHttpClient<AgentJwtMinter>(c => c.Timeout = TimeSpan.FromSeconds(cfg.HttpTimeoutSeconds));
// Bounded per-request ceiling on the gdrive-MCP round-trip (M6-finish stall guard).
// Previously Timeout.InfiniteTimeSpan — a hung gdrive-mcp call would stall the watcher
// forever (the M6 stall residual). A timeout now surfaces as TaskCanceledException,
// which McpGdriveClient's bounded retry + Downloader.IsTransient both treat as retryable.
builder.Services.AddHttpClient<GatewayMcpClient>(c => c.Timeout = TimeSpan.FromSeconds(cfg.HttpTimeoutSeconds));
builder.Services.AddSingleton<IDriveClient, McpGdriveClient>();

// --- durable state (on-CT Postgres, D4) ---
builder.Services.AddSingleton<IStateStore, PostgresStateStore>();

// --- ingest ---
builder.Services.AddSingleton(new BlobStore(cfg.StagingDir));
builder.Services.AddSingleton<IdempotencyLedger>();
builder.Services.AddSingleton<Downloader>();

// --- normalize ---
builder.Services.AddSingleton<Normalizer>();
builder.Services.AddSingleton<IManifestStore>(_ =>
    new YamlManifestStore(Path.Combine(cfg.RepoWorkingTree, "recordings", "manifest.yaml")));
builder.Services.AddSingleton(_ =>
    new ReadyMarker(Path.Combine(cfg.StagingDir, "inbox")));

// --- worker ---
builder.Services.AddSingleton<WatchWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WatchWorker>());

var app = builder.Build();

// Resolve the recordings folder id ONCE at startup (D3 scope filter) and hand it
// to the worker before it starts polling. FolderId (if set) skips resolution;
// otherwise the leaf of FolderPath (e.g. "cervello/recordings" -> "recordings")
// is resolved via gdrive_search_files. Fail-closed: an unresolvable/ambiguous
// folder throws here rather than the worker silently watching nothing (_folderId
// stays null -> IsInBoundary rejects everything).
{
    var drive = app.Services.GetRequiredService<IDriveClient>();
    var worker = app.Services.GetRequiredService<WatchWorker>();
    if (drive is McpGdriveClient mcpDrive)
    {
        var folderId = await mcpDrive.ResolveFolderIdAsync(CancellationToken.None);
        worker.SetFolderId(folderId);
        app.Logger.LogInformation("resolved recordings folder '{Path}' -> {FolderId}", cfg.FolderPath, folderId);
    }
}

// Opaque health heartbeat (health-checks). Carries NO recording data — the only
// thing that leaves the CT (invariant 3). 200 once the worker is up, else 503.
app.MapGet("/healthz", (WatchWorker w) =>
    w.Ready
        ? Results.Json(new { status = "ok" }, statusCode: 200)
        : Results.Json(new { status = "starting" }, statusCode: 503));

app.Urls.Add($"http://{cfg.HealthHost}:{cfg.HealthPort}");
app.Run();
