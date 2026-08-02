using Sinapsi.Nats;
using Sinapsi.SentinelConsole;

var builder = WebApplication.CreateBuilder(args);

// The read-model + live feed are process singletons; the bus subscriber fills them.
builder.Services.AddSingleton(new ReadModel(
    capacity: EnvInt("SENTINEL_CONSOLE_BUFFER", 2000)));
builder.Services.AddSingleton<LiveFeed>();
// Deploy-visibility lane: same pattern, separate read-model + subscriber.
builder.Services.AddSingleton(new DeployModel(
    capacity: EnvInt("SENTINEL_CONSOLE_DEPLOY_BUFFER", 500)));
builder.Services.AddSingleton(NatsConnectionOptions.FromEnvironment() with
{
    ClientName = "sinapsi-sentinel-console",
});
builder.Services.AddHostedService<SecurityBusSubscriber>();
builder.Services.AddHostedService<DeployBusSubscriber>();
// Operator Approval Bridge lane: same lifecycle-feed pattern, separate read-model +
// subscriber on security.approval.>.
builder.Services.AddSingleton(new ApprovalQueueModel(
    capacity: EnvInt("SENTINEL_CONSOLE_APPROVAL_BUFFER", 1000)));
builder.Services.AddHostedService<ApprovalBusSubscriber>();
// The Console's only path to the broker's command API — pending-queue read + approve/reject,
// both a transparent proxy (see BrokerClient doc comment). BRIDGE_BROKER_URL defaults to the
// broker's own default listen address (config.env.example: ASPNETCORE_URLS=http://0.0.0.0:8013).
builder.Services.AddHttpClient<BrokerClient>(c =>
{
    c.BaseAddress = new Uri(EnvStr("BRIDGE_BROKER_URL", "http://127.0.0.1:8013"));
    c.Timeout = TimeSpan.FromSeconds(5);
});
// Dev-only populated view (no live bus) when SENTINEL_CONSOLE_DEMO=1.
if (Environment.GetEnvironmentVariable("SENTINEL_CONSOLE_DEMO") == "1")
    builder.Services.AddHostedService<DemoSeeder>();
// Expose the subscribers' live health to /healthz + /api/stats.
builder.Services.AddSingleton(sp =>
    (SecurityBusSubscriber)sp.GetServices<IHostedService>()
        .First(s => s is SecurityBusSubscriber));
builder.Services.AddSingleton(sp =>
    (DeployBusSubscriber)sp.GetServices<IHostedService>()
        .First(s => s is DeployBusSubscriber));
builder.Services.AddSingleton(sp =>
    (ApprovalBusSubscriber)sp.GetServices<IHostedService>()
        .First(s => s is ApprovalBusSubscriber));

var app = builder.Build();

app.UseDefaultFiles();     // serve wwwroot/index.html at "/"
// PWA installability: the navigation shell, service worker and manifest MUST always be
// revalidated. ASP.NET Core's default static-file middleware emits ETag + Last-Modified but
// NO Cache-Control, so a browser applies *heuristic* freshness (≈10% of age since Last-Modified)
// and can keep serving a stale, cached pre-PWA index.html / sw.js without ever re-fetching —
// which is why a device that once loaded an older build never sees the current install-capable
// page (no manifest link / no registered SW → no "Install app"). The working Sinapsi Studio /
// Console PWAs serve these files with `Cache-Control: no-cache` (nginx); mirror that here so the
// shell is byte-revalidated on every load (cheap 304 via the existing ETag). Icons keep the
// default (immutable-ish, safe to cache). See the Sentinel PWA install investigation.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var name = ctx.File.Name; // for the default document at "/", this is "index.html"
        if (name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            || name.Equals("sw.js", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".webmanifest", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers.CacheControl = "no-cache";
        }
    }
});

// ── inspection API (read-only) ───────────────────────────────────
appapp:
app.MapGet("/api/posture", (ReadModel rm) => Results.Json(rm.Posture()));
app.MapGet("/api/recent", (ReadModel rm, int? n) => Results.Json(rm.Recent(Math.Clamp(n ?? 200, 1, 2000))));
app.MapGet("/api/chain/{id}", (ReadModel rm, string id) => Results.Json(rm.Chain(id)));

// Deploy-visibility lane — "did my merge actually deploy?" without SSH.
app.MapGet("/api/deploys", (DeployModel dm, int? n) => Results.Json(dm.Recent(Math.Clamp(n ?? 200, 1, 2000))));
app.MapGet("/api/deploy-state", (DeployModel dm) => Results.Json(dm.State()));

// ── Operator Approval Bridge lane ───────────────────────────────────────────────
// The operator identity the Console approves/rejects AS. Server-side-configured, never taken from
// the browser request — a client cannot spoof a different approver identity through this surface.
// This is a placeholder until real per-operator SSO-bound identity lands with live approve-channel
// authz (deferred); the broker still independently enforces
// approver_identity != requester_identity regardless of what this value is.
var operatorIdentity = EnvStr("SENTINEL_CONSOLE_OPERATOR_IDENTITY", "operator:console");

// The pending queue — READ-ONLY proxy to the broker's GET /pending: title + typed params +
// provenance per open request. Never mutates broker state.
app.MapGet("/api/approvals/pending", async (BrokerClient bc, CancellationToken ct) =>
{
    var result = await bc.GetPendingAsync(ct);
    return Results.Json(new { brokerReachable = result.BrokerReachable, items = result.Items });
});

// Approve / Reject — forwarded VERBATIM to the broker's COMMAND API (single receiver, rejectable).
// The Console performs no security check of its own here; it is a transparent relay so the broker's
// one-shot / self-approval / CAS enforcement is the only enforcement that exists.
app.MapPost("/api/approvals/approve", async (ApprovalActionBody body, BrokerClient bc, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.request_id)) return Results.BadRequest(new { error = "request_id is required" });
    var r = await bc.ApproveAsync(body.request_id, operatorIdentity, ct);
    return r.Reached
        ? Results.Content(r.RawBody, "application/json", statusCode: r.StatusCode)
        : Results.Json(new { error = "approval-bridge broker unreachable" }, statusCode: 502);
});
app.MapPost("/api/approvals/reject", async (ApprovalActionBody body, BrokerClient bc, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.request_id)) return Results.BadRequest(new { error = "request_id is required" });
    var r = await bc.RejectAsync(body.request_id, operatorIdentity, ct);
    return r.Reached
        ? Results.Content(r.RawBody, "application/json", statusCode: r.StatusCode)
        : Results.Json(new { error = "approval-bridge broker unreachable" }, statusCode: 502);
});

// The lifecycle feed (audit trail) — from the BUS, not the broker: requested→approved/rejected→
// executed/expired, correlation-id joined (requirement 3).
app.MapGet("/api/approvals/recent", (ApprovalQueueModel aqm, int? n) => Results.Json(aqm.Recent(Math.Clamp(n ?? 200, 1, 2000))));
app.MapGet("/api/approvals/chain/{id}", (ApprovalQueueModel aqm, string id) => Results.Json(aqm.Chain(id)));

app.MapGet("/api/stats", (
    ReadModel rm, LiveFeed feed, SecurityBusSubscriber sub, DeployModel dm, DeployBusSubscriber dsub,
    ApprovalQueueModel aqm, ApprovalBusSubscriber asub) =>
    Results.Json(new
    {
        total = rm.Total, ingested = sub.Ingested, connected = sub.Connected, clients = feed.ClientCount,
        deploysTotal = dm.Total, deploysIngested = dsub.Ingested, deployBusConnected = dsub.Connected,
        approvalsTotal = aqm.Total, approvalsIngested = asub.Ingested, approvalBusConnected = asub.Connected,
    }));

app.MapGet("/healthz", (SecurityBusSubscriber sub) =>
    sub.Connected ? Results.Ok("ok") : Results.Json(new { status = "degraded", reason = "bus not connected" }, statusCode: 503));

// ── live feed (Server-Sent Events) ────────────────────────────────
app.MapGet("/events", async (HttpContext ctx, LiveFeed feed, CancellationToken ct) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers.Connection = "keep-alive";
    using var sub = feed.Subscribe();
    await ctx.Response.WriteAsync(": connected\n\n", ct);
    await ctx.Response.Body.FlushAsync(ct);
    try
    {
        await foreach (var d in sub.ReadAllAsync(ct))
        {
            var json = System.Text.Json.JsonSerializer.Serialize(d,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);
        }
    }
    catch (OperationCanceledException) { /* client disconnected */ }
});

var url = $"http://0.0.0.0:{EnvInt("SENTINEL_CONSOLE_PORT", 8140)}";
app.Run(url);

static int EnvInt(string k, int dflt) =>
    int.TryParse(Environment.GetEnvironmentVariable(k), out var v) && v > 0 ? v : dflt;

static string EnvStr(string k, string dflt) =>
    Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : dflt;

internal sealed record ApprovalActionBody(string? request_id);
