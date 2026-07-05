using Cervello.Watcher;
using Cervello.Watcher.Drive;
using Cervello.Watcher.Ingest;
using Cervello.Watcher.Normalize;
using Cervello.Watcher.State;
using Microsoft.Extensions.Logging;

// -----------------------------------------------------------------------------
// Cervello.Watcher host. Like Sinapsi.Indexer's isolated shape, this binary opens
// NO NATS connection: it references neither Sinapsi.Nats nor any NATS client
// (invariant 3 / D8). The ONLY thing that leaves the CT is an opaque health
// heartbeat; audio + recording data stay on-CT (custody). All Google egress is
// forced through the CT proxy by ProxyHttpClientFactory (D2).
// -----------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);

var cfg = WatcherConfig.FromEnvironment();
builder.Services.AddSingleton(cfg);

// --- Drive seam (read-only SA + proxy, D1/D2) ---
builder.Services.AddSingleton(_ => DriveClientFactory.Create(cfg));
builder.Services.AddSingleton<IDriveClient, GoogleDriveClient>();

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

// Opaque health heartbeat (health-checks). Carries NO recording data — the only
// thing that leaves the CT (invariant 3). 200 once the worker is up, else 503.
app.MapGet("/healthz", (WatchWorker w) =>
    w.Ready
        ? Results.Json(new { status = "ok" }, statusCode: 200)
        : Results.Json(new { status = "starting" }, statusCode: 503));

app.Urls.Add($"http://{cfg.HealthHost}:{cfg.HealthPort}");
app.Run();
