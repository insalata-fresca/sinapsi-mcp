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

// ── the FULL end-to-end pipeline (E-PIPE): every stage + the EnrichmentPipeline
//    orchestrator the drain worker now runs, threading a `normalized` recording all
//    the way to `graph_pr_opened`/escalated. AddCervelloPipeline registers the stages
//    + orchestrator; the three input seams it needs — IAudioSource (the transient
//    recording audio from the CT staging blob), IPriorSource (filename+manifest prior),
//    IRecordingFactSource (derived summary/links/timeline/attention/participants/garbled
//    spans) — are now registered by AddCervelloEnrichment's LIVE branch (L-SEAMS): the
//    StagingBlobAudioSource / ManifestPriorSource / BrainApiRecordingFactSource live
//    adapters. So a live host needs no per-seam wiring here.
//
//    REMAINING L2 WIRING (the one seam L-SEAMS could not close without the live gateway):
//    IExternalBlobFetcher — the drive://gmail:// evidence fetcher CtPinStore needs for
//    pin://-on-cite. Its live adapter calls the gdrive/gmail MCP through the CT121
//    agentgateway (a scoped machine identity), so it is a genuine deploy concern. The L2
//    deploy MUST register it before this host resolves the pipeline; it is deliberately
//    NOT stub-faked (a fake would silently pin garbage). See CompositionCompletenessTests.
builder.Services.AddCervelloPipeline();

// ── the DIALOGUE-INTERACTION backend (S50 §5): the context-pack assembler + map-read + capture/goal
//    services the NEW bridge tools call (cervello_context_pack / _search / _get / _timeline_walk /
//    _capture_fact / _set_goal / _link_evidence). Wired AFTER AddCervelloEnrichment so it can reuse
//    the engine's IOpenPointStore (pack open-points piggyback) + CervelloGraphWriter (goal/evidence
//    review-PR). Live = indexer(:8009) + repo map graph + Pg delta cursor + repo deposit; fake =
//    in-memory (the test harness wires the index/graph fakes). ─────────────────────────────────────
builder.Services.AddCervelloContextPack(engineCfg);

// ── L2 seam: IExternalBlobFetcher — the ONE port AddCervelloEnrichment leaves for the
//    deploy to register (the drive://gmail:// evidence fetcher CtPinStore needs for
//    pin://-on-cite). The RECORDINGS drain path never cites external evidence, so this is
//    never reached when enriching a recording; we register a CLEAR-THROWING adapter so the
//    FULL live graph resolves (host boots, pipeline constructible) WITHOUT silently pinning
//    garbage. The live agentgateway-backed adapter is deferred to the doc/mail ingestion
//    mission; if this ever throws it is the signal to wire it, never to fake it. Live-only:
//    in fake mode the engine wires no CtPinStore, so IExternalBlobFetcher is never resolved.
if (engineCfg.UseLiveAdapters)
    builder.Services.AddSingleton<IExternalBlobFetcher>(new NotConfiguredExternalBlobFetcher());

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
// Every live Pg adapter owns table(s) that must exist before the worker drains. In live mode
// we ensure EVERY registered ISchemaInitializer — enrichment_ledger (idempotency, §5/§8),
// correction_map (glossary), voiceprints + tombstones + enrollment-audio (attribution), and
// open_points (escalations) — so a fresh CT146 boots clean. (MIGRATE-FIX: the host used to
// ensure ONLY enrichment_ledger, so a fresh CT146 was missing correction_map / voiceprints /
// open_points and the CorrectionStage crashed with `42P01 relation "correction_map" does not
// exist`.) The tables are independent (no cross-adapter FKs), so any order is correct.
//
// Fail-closed: EnsureSchemaAsync retries transient connection failures internally; a hard/
// persistent error propagates out of this await and aborts startup rather than draining against
// a half-migrated DB. (The `watcher_recording` table the drain reads is owned + ensured by the
// Watcher — we never CREATE it here.) In fake mode no ISchemaInitializer is registered (the
// in-memory stores have no schema), so this loop is a no-op.
if (engineCfg.UseLiveAdapters)
{
    var initializers = app.Services.GetServices<ISchemaInitializer>().ToList();
    foreach (var init in initializers)
    {
        await init.EnsureSchemaAsync(CancellationToken.None);
        app.Logger.LogInformation("{Schema} schema ensured on startup", init.SchemaName);
    }
    app.Logger.LogInformation("all {Count} Pg adapter schemas ensured on startup", initializers.Count);
}

// ── opaque health heartbeat (mirror the Watcher). Carries NO recording data —
//    the only thing that leaves the CT (invariant 3). 200 once the worker is up. ──
app.MapGet("/healthz", (DrainWorker w) =>
    w.Ready
        ? Results.Json(new { status = "ok" }, statusCode: 200)
        : Results.Json(new { status = "starting" }, statusCode: 503));

// ── open-points HTTP surface (S50 L3 exposure) ──────────────────────────────────────────────
//    Token-gated GET /open-points + POST /open-points/{id}/answer, fronting OpenPointsService so
//    the operator's claude.ai app (Surface A via the CT145 bridge) can list + answer open-points.
//    LIVE mode only: the service + its write-back seams are wired by AddCervelloEnrichment's live
//    branch; in fake mode OpenPointsService is unresolvable (no graph-writer / enrollment source),
//    so the routes are mapped only when live. The bearer gate fails CLOSED on an empty token.
if (engineCfg.UseLiveAdapters)
    app.MapOpenPoints();

// ── context-pack + map-read + capture/goal HTTP surfaces (S50 §5 exposure) ──────────────────────────
//    Token-gated (the SAME cervello operator bearer as open-points, via IOpenPointsAuthGate — fail-
//    closed on an empty token). LIVE mode only: the assembler + capture/goal services + their live
//    seams (indexer, repo map graph, graph-writer, delta cursor) are wired by the live branches; in
//    fake mode the graph/index seams are unresolvable (a fake-mode host wires its own), so the routes
//    map only when live. The bridge (CT145) is the only caller, via the CT146 ingress allow.
if (engineCfg.UseLiveAdapters)
{
    app.MapContextPack(engineCfg.PackBudgetDefault);
    app.MapCapture();
}

app.Urls.Add($"http://{hostCfg.HealthHost}:{hostCfg.HealthPort}");
app.Run();

/// <summary>Exposed so the host test project can reference the entry assembly (WebApplicationFactory-free tests).</summary>
public partial class Program;
