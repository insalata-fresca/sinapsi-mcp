using Microsoft.Extensions.Logging;
using Sinapsi.Indexer;
using Sinapsi.Nats;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);

// -----------------------------------------------------------------------
// Capability composition (indexer-generalization,
// docs/architecture/indexer-generalization.md). Every INDEXER_CAP_* +
// INDEXER_NATS_MODE knob defaults to today's bundled behaviour when unset
// (index+search.mcp+search.http+learn_publish=on, shared-bus) — so deploying
// THIS image with an unchanged config.env is a behavioural no-op.
//
// The load-bearing rule: a DISABLED capability is constructed NOTHING —
// no route, no MCP tool, no NATS connection/consumer/seed, no identity.
// IndexerCapabilities resolves the flags into a plain, unit-testable
// composition decision; everything below just acts on it.
// -----------------------------------------------------------------------
var caps = IndexerCapabilities.FromEnvironment();

builder.Services.AddSingleton<IIndexStore, PostgresIndexStore>();
builder.Services.AddSingleton<IEmbedder, OnnxEmbedder>();
builder.Services.AddSingleton(sp => new SourceScanner(
    SourceScanner.ReposFromEnv(),
    Environment.GetEnvironmentVariable("FORGE_REPO_TOKEN"),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<SourceScanner>()));

// --- index capability ---
// SharedBusConsumer: the NATS-consuming IndexerWorker (push-coalesced +
// periodic rescan) — the only shape that needs a NatsConnectionOptions for
// indexing. TimerOnly: TimerOnlyIndexWorker — SAME reindex engine, but no
// NatsConnectionOptions is ever constructed for it, so the process opens no
// NATS connection for indexing. None: neither type is registered — no
// scanner loop, no git-pull, no upsert path, nothing.
switch (caps.WorkerShape)
{
    case IndexWorkerShape.SharedBusConsumer:
        builder.Services.AddSingleton(NatsConnectionOptions.FromEnvironment() with { ClientName = "sinapsi-indexer" });
        builder.Services.AddSingleton<IndexerWorker>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<IndexerWorker>());
        break;
    case IndexWorkerShape.PrivateSubjectConsumer:
        // cervello-private-events.md OPTION-1 §4.2.4 — config-layer (secondary)
        // belt-and-braces bar: fail closed at startup if the configured subject/
        // stream is not the cervello private tree/stream. The PRIMARY bar is the
        // auth layer (the scoped cervello-indexer nkey's subscribe set refuses
        // anything outside homelab.cervello.> at the server) — this just stops a
        // fat-fingered config from even attempting the subscribe. Reuses the exact
        // same IndexerWorker/FetchAsync engine as shared-bus; only the env-driven
        // subject/stream/identity differ (already fully parameterized).
        IndexerConfig.ValidatePrivateSubjectAndStream(
            watchSubject: Environment.GetEnvironmentVariable("INDEXER_WATCH_SUBJECT") ?? "events.git.>",
            stream: Environment.GetEnvironmentVariable("INDEXER_STREAM") ?? "EVENTS",
            allowedSubjectPrefix: "homelab.cervello.",
            allowedStream: "CERVELLO_AUDIT");
        builder.Services.AddSingleton(NatsConnectionOptions.FromEnvironment() with { ClientName = "sinapsi-indexer-cervello" });
        builder.Services.AddSingleton<IndexerWorker>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<IndexerWorker>());
        break;
    case IndexWorkerShape.TimerOnly:
        builder.Services.AddSingleton<TimerOnlyIndexWorker>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<TimerOnlyIndexWorker>());
        break;
    case IndexWorkerShape.None:
        break;
}

// --- learn-publish capability (the ONLY shared-bus WRITE capability) ---
// Disabled: LearnPublisher is never constructed, so LEARN_NATS_NKEY /
// LEARN_NATS_SEED_PATH are never read and no publish connection is ever
// opened. The publish_learning tool is likewise never registered (below).
if (caps.LearnPublish)
    builder.Services.AddSingleton<LearnPublisher>();

// --- MCP surface, served at /mcp on the same Kestrel as the health endpoint ---
// Tool types come from caps.McpToolTypes() (a runtime type LIST, not the
// compile-time generic .WithTools<T>()) so a disabled capability contributes
// NO type to the MCP server at all — not merely an unreachable one.
// The tool-registration overload here is a KNOWN footgun (passing a concrete
// list type positionally binds the wrong WithTools overload and registers ZERO
// tools at runtime). It is factored into McpComposition.AddIndexerMcp so the
// runtime boot smoke (McpBootSmokeTests) exercises THIS EXACT registration path.
builder.Services.AddIndexerMcp(caps);

var app = builder.Build();

app.MapMcp("/mcp");

// Health endpoint (liveness probe). Reports ONLY the capabilities that are
// actually enabled — e.g. an isolated search-only tenant's health must not
// fail on nats_ready (there is no NATS connection to be ready).
app.MapGet("/", async (IServiceProvider sp, IIndexStore store) =>
{
    var dbOk = false;
    try { await store.PingAsync(CancellationToken.None); dbOk = true; } catch { /* report below */ }

    bool? natsReady = null;
    bool? schemaReady = null;
    long? docsUpserted = null;
    long? docsEmbedded = null;
    DateTimeOffset? lastReindex = null;

    switch (caps.WorkerShape)
    {
        case IndexWorkerShape.SharedBusConsumer:
        case IndexWorkerShape.PrivateSubjectConsumer:
        {
            var w = sp.GetRequiredService<IndexerWorker>();
            natsReady = w.Ready;
            schemaReady = w.SchemaReady;
            docsUpserted = w.DocsUpserted;
            docsEmbedded = w.DocsEmbedded;
            lastReindex = w.LastReindex;
            break;
        }
        case IndexWorkerShape.TimerOnly:
        {
            var w = sp.GetRequiredService<TimerOnlyIndexWorker>();
            // No NATS connection exists in isolated mode — nats_ready is
            // intentionally omitted (null) rather than reported false/true.
            schemaReady = w.SchemaReady;
            docsUpserted = w.DocsUpserted;
            docsEmbedded = w.DocsEmbedded;
            lastReindex = w.LastReindex;
            break;
        }
        case IndexWorkerShape.None:
            break;
    }

    // Overall "ok": dbOk always required; schemaReady/natsReady only gate
    // readiness for the capabilities that are actually enabled.
    var ok = dbOk && (schemaReady ?? true) && (natsReady ?? true);
    return Results.Json(new
    {
        status = ok ? "ok" : "starting",
        service = "sinapsi-indexer",
        capabilities = new
        {
            index = caps.Index,
            search_mcp = caps.SearchMcp,
            search_http = caps.SearchHttp,
            learn_publish = caps.LearnPublish,
            nats_mode = caps.NatsMode switch
            {
                IndexerNatsMode.Isolated => "isolated",
                IndexerNatsMode.Private => "private",
                _ => "shared-bus",
            },
        },
        nats_ready = natsReady,
        schema_ready = schemaReady,
        db_ok = dbOk,
        docs_upserted = docsUpserted,
        docs_embedded = docsEmbedded,
        last_reindex = lastReindex,
    }, statusCode: ok ? 200 : 503);
});

// HTTP search endpoint — M-B3, token-gated (M5-secure fix). Mounted ONLY when
// the search.http capability is enabled; disabled ⇒ the route is never
// registered at all (404, same as today's INDEXER_SEARCH_TOKEN-unset
// behaviour, but now capability-gated).
//
// M5-secure: INDEXER_SEARCH_TOKEN was previously read NOWHERE — the route was
// wide open to any caller once mounted. Every request now MUST present
// "Authorization: Bearer <INDEXER_SEARCH_TOKEN>" (constant-time compare via
// SearchAuth); missing/mismatched => 401 BEFORE the store is ever touched.
//
// GET /search?q=<websearch query>[&limit=<1-30>][&source=<logical source name>]
// Returns ranked FTS hits (ts_rank_cd, ts_headline snippets).
// Tombstoned and secret-path rows are excluded IN THE STORE SQL (defence-in-depth).
if (caps.SearchHttp)
{
    var searchToken = IndexerConfig.SearchToken();

    app.MapGet("/search", async (
        HttpContext ctx,
        IIndexStore store,
        string? q,
        string? limit,
        string? source,
        CancellationToken cancellationToken) =>
    {
        var authHeader = ctx.Request.Headers.Authorization.FirstOrDefault();
        if (!SearchAuth.IsAuthorized(authHeader, searchToken))
        {
            await SearchAuth.WriteUnauthorizedAsync(ctx);
            return;
        }

        var (req, err) = SearchRequest.TryParse(q, limit, source);
        if (err is not null)
        {
            await Results.Json(new { error = err }, statusCode: 400).ExecuteAsync(ctx);
            return;
        }

        try
        {
            var hits = await store.SearchAsync(req!.Query, req.Source, kind: null, req.Limit, cancellationToken);
            var items = hits
                .Select(h => new SearchResultItem(h.Source, h.Path, h.Kind, h.Title, h.Scope, h.Snippet, h.Score))
                .ToList();
            await Results.Json(new SearchResponse(req.Query, items.Count, items)).ExecuteAsync(ctx);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            // Surface a scrubbed, capped error — never a raw connection string or token.
            await Results.Json(new { error = IndexerErrors.FromException(e) }, statusCode: 500).ExecuteAsync(ctx);
        }
    }).WithName("SearchIndex").WithTags("search");
}

var host = Environment.GetEnvironmentVariable("INDEXER_HEALTH_HOST") ?? "0.0.0.0";
// Fail-closed: a non-numeric / out-of-range INDEXER_HEALTH_PORT throws here
// (naming the var) instead of letting Kestrel reject it opaquely at bind time.
var port = IndexerConfig.HealthPort();
app.Urls.Add($"http://{host}:{port}");

app.Run();
