using Microsoft.Extensions.Logging;
using Sinapsi.Indexer;
using Sinapsi.Nats;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);

builder.Services.AddSingleton(NatsConnectionOptions.FromEnvironment() with { ClientName = "sinapsi-indexer" });
builder.Services.AddSingleton<IIndexStore, PostgresIndexStore>();
builder.Services.AddSingleton<IEmbedder, OnnxEmbedder>();
builder.Services.AddSingleton(sp => new SourceScanner(
    SourceScanner.ReposFromEnv(),
    Environment.GetEnvironmentVariable("FORGE_REPO_TOKEN"),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<SourceScanner>()));
builder.Services.AddSingleton<IndexerWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IndexerWorker>());
builder.Services.AddSingleton<LearnPublisher>();

// MCP surface, served at /mcp on the same Kestrel as the health endpoint.
//   read:  search_index + semantic_search + get_learning (IndexTools)
//   write: publish_learning (LearnTools) — emits a learning-published event.
builder.Services
    .AddMcpServer(o => o.ServerInfo = new() { Name = "sinapsi-indexer", Version = "1.0.0" })
    // Stateless transport strips a forwarded Mcp-Session-Id so a fronting proxy can't 400 it.
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<IndexTools>()
    .WithTools<LearnTools>();

var app = builder.Build();

app.MapMcp("/mcp");

// Health endpoint (liveness probe). Also proves Postgres reachability.
app.MapGet("/", async (IndexerWorker w, IIndexStore store) =>
{
    var dbOk = false;
    try { await store.PingAsync(CancellationToken.None); dbOk = true; } catch { /* report below */ }
    var ok = w.Ready && w.SchemaReady && dbOk;
    return Results.Json(new
    {
        status = ok ? "ok" : "starting",
        service = "sinapsi-indexer",
        nats_ready = w.Ready,
        schema_ready = w.SchemaReady,
        db_ok = dbOk,
        docs_upserted = w.DocsUpserted,
        docs_embedded = w.DocsEmbedded,
        last_reindex = w.LastReindex,
    }, statusCode: ok ? 200 : 503);
});

var host = Environment.GetEnvironmentVariable("INDEXER_HEALTH_HOST") ?? "0.0.0.0";
// Fail-closed: a non-numeric / out-of-range INDEXER_HEALTH_PORT throws here
// (naming the var) instead of letting Kestrel reject it opaquely at bind time.
var port = IndexerConfig.HealthPort();
app.Urls.Add($"http://{host}:{port}");

app.Run();
