using ApprovalBridge.Broker;
using ApprovalBridge.Broker.Core;
using ApprovalBridge.Broker.Events;
using ApprovalBridge.Broker.Registry;
using ApprovalBridge.Broker.Store;
using ApprovalBridge.Executor.Dispatch;
using ApprovalBridge.Executor.Garmin;
using NATS.Client.Core;
using Sinapsi.Nats;
using Sinapsi.Nats.EventPlane;

// ── The Operator Approval Bridge broker (E1.3), SHADOW / deny-by-default. ────────────────────────
// It holds coordination authority but never a target secret; dispatch goes through the C2
// NullActCommandDispatcher so it acts on nothing. Live approve-authz (E1.5) and the executor (E1.4)
// are out of scope — this service is deployed DORMANT.
var cfg = BrokerConfig.FromEnvironment();

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(cfg);

// Allowlist (E1.1). An empty/missing dir ⇒ deny-everything, valid for a dormant deploy.
builder.Services.AddSingleton<IActionRegistry>(_ =>
    string.IsNullOrEmpty(cfg.ActionsDir) || !Directory.Exists(cfg.ActionsDir)
        ? new InMemoryActionRegistry([])
        : YamlActionLoader.LoadDirectory(cfg.ActionsDir));

// Dispatch seam (I3/§5 (5)). DEFAULT = the C2 NullActCommandDispatcher (deny-by-default / dormant): the
// broker acts on nothing. Only when BRIDGE_EXECUTOR_LIVE=true is IActCommandDispatcher bound to the real
// E1.4 target-side ExecutorDispatcher (via ExecutorWiring.SelectDispatcher). Flipping that flag is a
// trust-boundary flip (always-escalate, docs/66 §10) and is out of scope for E1.4 — the live Garmin network
// integration is intentionally left un-provisioned (NotProvisioned*), so even a flipped flag executes nothing.
builder.Services.AddSingleton<IActCommandDispatcher>(_ => ExecutorWiring.SelectDispatcher(
    live: cfg.ExecutorLive,
    actionsDir: cfg.ActionsDir,
    secretsRootDir: cfg.ExecutorSecretsRoot,
    handlers: [new GarminOAuthExchangeExecutor(new NotProvisionedGarminEndpoint(), new NotProvisionedGarminTokenStore())]));
builder.Services.AddSingleton<IRateLimiter, InMemoryRateLimiter>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<INonceSource, CryptoNonceSource>();

if (cfg.ShadowLocalOnly)
{
    // Fully dormant: in-memory KV + logging emitter, NATS never touched.
    builder.Services.AddSingleton<IApprovalStore, InMemoryApprovalStore>();
    builder.Services.AddSingleton<IDeadLetterSink, LoggingDeadLetterSink>();
    builder.Services.AddSingleton<IApprovalEventEmitter, LoggingApprovalEventEmitter>();
}
else
{
    // Live wiring (a later, operator-gated deploy step): durable JetStream KV + CloudEvents emitter.
    var nats = new NatsConnection(cfg.Nats.BuildNatsOpts());
    var store = await JetStreamKvApprovalStore.ConnectAsync(nats, cfg.KvBucket);
    var publisher = await NatsEventPublisher.ConnectAsync(cfg.Nats, cfg.EventSource);
    builder.Services.AddSingleton<NatsConnection>(nats);
    builder.Services.AddSingleton<NatsEventPublisher>(publisher);
    builder.Services.AddSingleton<IApprovalStore>(store);
    builder.Services.AddSingleton<IDeadLetterSink, LoggingDeadLetterSink>();
    builder.Services.AddSingleton<IApprovalEventEmitter, NatsApprovalEventEmitter>();
}

builder.Services.AddSingleton<BridgeBroker>();

var app = builder.Build();

// Liveness (unauthenticated). Advertises the shadow posture so no one mistakes it for live.
app.MapGet("/health", (BrokerConfig c, IActionRegistry reg, IActCommandDispatcher disp) => Results.Json(new
{
    status = "ok",
    version = BrokerConfig.Version,
    shadow = !c.ExecutorLive,
    dispatch = disp is NullActCommandDispatcher
        ? "deny-by-default (NullActCommandDispatcher)"
        : "executor (ExecutorDispatcher)",
    actions = reg.ActionIds.Count,
}));

// Request intake — the EVENT (a fact). Agent-facing binding (MCP tool) is E1.6; here the identity is
// carried in the body for the shadow surface. Deny-by-default on unknown action / bad params.
app.MapPost("/request", async (RequestBody body, BridgeBroker broker, CancellationToken ct) =>
{
    var o = await broker.RequestAsync(body.action_id ?? "", body.@params ?? "{}", body.requester_identity ?? "", ct);
    return o.Accepted
        ? Results.Json(new { request_id = o.RequestId })
        : Results.Json(new { rejected = o.Reason.ToString() }, statusCode: 422);
});

// Approve / reject — the COMMAND (single receiver = this broker, rejectable). Live authz that only the
// operator identity may call is E1.5 (deferred); in shadow the dispatcher is deny-by-default so nothing acts.
app.MapPost("/approve", async (ApproveBody body, BridgeBroker broker, CancellationToken ct) =>
{
    var o = await broker.ApproveAsync(body.request_id ?? "", body.approver_identity ?? "", body.nonce, ct);
    return o.Accepted
        ? Results.Json(new { dispatched = o.Dispatched, executor_accepted = o.ExecutorAccepted, detail = o.Detail, result = o.ResultJson })
        : Results.Json(new { rejected = o.Reason.ToString() }, statusCode: 409);
});

app.MapPost("/reject", async (ApproveBody body, BridgeBroker broker, CancellationToken ct) =>
{
    var o = await broker.RejectAsync(body.request_id ?? "", body.approver_identity ?? "", ct);
    return o.Accepted ? Results.Json(new { rejected = true }) : Results.Json(new { rejected = o.Reason.ToString() }, statusCode: 409);
});

app.Run();

internal sealed record RequestBody(string? action_id, string? @params, string? requester_identity);
internal sealed record ApproveBody(string? request_id, string? approver_identity, string? nonce);
