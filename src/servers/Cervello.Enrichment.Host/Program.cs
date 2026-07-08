using Cervello.Enrichment;
using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Host;
using Cervello.Enrichment.Host.Drain;
using Cervello.Enrichment.Pipeline.Stages;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.DependencyInjection;

// -----------------------------------------------------------------------------
// Cervello.Enrichment.Host — the deploy-slice HOST for the enrichment engine
// (the binary L2 deploys to CT146-cervello). It hosts AddCervelloEnrichment and
// runs a BACKGROUND WORKER that DRAINS recordings the M6 Cervello.Watcher has
// driven to `normalized` (the shared SCHEMAS §5 state row), runs the pipeline
// entry (ingest) under the escalate-only gate, and advances the state.
//
// Like the engine + the Watcher (invariant 3 / D8) this binary opens NO NATS
// connection: it references neither Sinapsi.Nats nor any NATS client. The ONLY
// thing that leaves the CT is an opaque health heartbeat; recording data stays
// on-CT (custody).
//
// Escalate-only + map-PR dry-run are the DEFAULTS (EnrichmentConfig:
// CERVELLO_GRADED_AUTO_APPLY=false, CERVELLO_MAP_PR_DRY_RUN=true) — the engine's
// composition root enforces the phase gate; the host flips no auto-apply.
// -----------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);

// ── engine + host config (both env-driven, fail-closed) ──────────────────────
var engineCfg = EnrichmentConfig.FromEnvironment();
var hostCfg = HostConfig.FromEnvironment();
builder.Services.AddSingleton(hostCfg);

// ── the enrichment engine's DI composition root (live adapters behind the flag;
//    escalate-only unless CERVELLO_GRADED_AUTO_APPLY=true) ─────────────────────
builder.Services.AddCervelloEnrichment(engineCfg);

// ── the pipeline ENTRY stage (ingest) the drain worker drives. The stage takes
//    the engine's IEnrichmentLedger (idempotency) — resolved from the composition
//    root above. Later stages are NOT threaded here (see DrainWorker scope note). ─
builder.Services.AddSingleton<IngestStage>();

// ── drain source: the read-only view over the Watcher-written `normalized` rows.
//    Live = the CT146 Postgres table; fake = in-memory (offline slice / a host that
//    wires its own). The choice tracks the engine's live-adapter flag so a fake-mode
//    host stays fully offline (no DB), mirroring the composition root's seam. ──────
if (engineCfg.UseLiveAdapters)
    builder.Services.AddSingleton<INormalizedWorkQueue, PgNormalizedWorkQueue>();
else
    builder.Services.AddSingleton<INormalizedWorkQueue, InMemoryNormalizedWorkQueue>();

// ── the drain worker (BackgroundService) ─────────────────────────────────────
builder.Services.AddSingleton<DrainWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DrainWorker>());

var app = builder.Build();

// ── migrations on startup ────────────────────────────────────────────────────
// The engine's ledger owns the enrichment_ledger table (idempotency, §5/§8). In live
// mode we ensure its schema before the worker starts draining, so a fresh CT146 boots
// clean. (The `watcher_recording` table the drain reads is owned + ensured by the
// Watcher — we never CREATE it here; only its own EnsureSchemaAsync does.) In fake mode
// the in-memory ledger has no schema, so this is a no-op.
if (engineCfg.UseLiveAdapters)
{
    var ledger = app.Services.GetRequiredService<IEnrichmentLedger>();
    if (ledger is PgEnrichmentLedger pg)
    {
        await pg.EnsureSchemaAsync(CancellationToken.None);
        app.Logger.LogInformation("enrichment_ledger schema ensured on startup");
    }
}

// ── opaque health heartbeat (mirror the Watcher). Carries NO recording data —
//    the only thing that leaves the CT (invariant 3). 200 once the worker is up. ──
app.MapGet("/healthz", (DrainWorker w) =>
    w.Ready
        ? Results.Json(new { status = "ok" }, statusCode: 200)
        : Results.Json(new { status = "starting" }, statusCode: 503));

app.Urls.Add($"http://{hostCfg.HealthHost}:{hostCfg.HealthPort}");
app.Run();

/// <summary>Exposed so the host test project can reference the entry assembly (WebApplicationFactory-free tests).</summary>
public partial class Program;
