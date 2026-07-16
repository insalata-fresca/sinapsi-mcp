using DeliveryEvaluator.Host;
using Sinapsi.Nats;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);

// ── NATS connection (NKey+TLS from env) ────────────────────────────────────────────────────────
// One identity, publish-scoped at the server ACL to homelab.security.authz.> (verdict facts) and
// subscribe-scoped to the merge/deploy change stream. The evaluator therefore CANNOT publish onto
// the act tree even if a bug tried — observe-only is enforced structurally AND at the ACL.
var opts = NatsConnectionOptions.FromEnvironment() with { ClientName = "delivery-evaluator" };

// Eager connect the fact publisher (fail-fast; the Quadlet Restart=on-failure retries a cold bus).
var source = Environment.GetEnvironmentVariable("DELIVERY_EVALUATOR_SOURCE")
             ?? "delivery-evaluator://ct121-mcp-gateway/";
var publisher = await NatsVerdictFactPublisher.ConnectAsync(opts, source);

builder.Services.AddSingleton<IVerdictFactPublisher>(publisher);
builder.Services.AddSingleton(opts);
builder.Services.AddSingleton<DeliveryEvaluatorWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DeliveryEvaluatorWorker>());

var app = builder.Build();

// Liveness/readiness: ready once the durable consumer is bound. Reports the shadow posture and the
// verdict counters so an operator can see the harvest is flowing without SSH.
app.MapGet("/", (DeliveryEvaluatorWorker w) => Results.Json(new
{
    status = w.Ready ? "ok" : "starting",
    service = "delivery-evaluator",
    posture = "shadow-observe-only",
    stream = Environment.GetEnvironmentVariable("EVALUATOR_STREAM") ?? "HOMELAB_AUDIT",
    watch_subjects = DeliveryEvaluatorWorker.WatchSubjects(),
    verdict_fact_root = "homelab.security.authz.delivery-evaluator.<verdict>.delivery-risk-evaluator",
    consumer_ready = w.Ready,
    events_processed = w.EventsProcessed,
    verdicts_published = w.VerdictsPublished,
    dead_lettered = w.DeadLettered,
    last_verdict_at = w.LastVerdictAt,
}, statusCode: w.Ready ? 200 : 503));

app.MapGet("/healthz", (DeliveryEvaluatorWorker w) =>
    w.Ready ? Results.Ok("ok")
            : Results.Json(new { status = "starting", reason = "durable consumer not yet bound" }, statusCode: 503));

var host = Environment.GetEnvironmentVariable("DELIVERY_EVALUATOR_HEALTH_HOST") ?? "0.0.0.0";
var port = int.TryParse(Environment.GetEnvironmentVariable("DELIVERY_EVALUATOR_HEALTH_PORT"), out var p) && p > 0
    ? p : 8014;
app.Urls.Add($"http://{host}:{port}");

app.Run();

// Exposed so the WebApplicationFactory-free host test can reference the assembly.
public partial class Program;
