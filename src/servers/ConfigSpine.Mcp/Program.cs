using ConfigSpine.Mcp;
using Sinapsi.Mcp;
using Sinapsi.Nats;

// ----------------------------------------------------------------------------------------------
// ConfigSpine.Mcp host. A NARROW, scoped MCP surface that lets an agent self-record a config
// mutation it just made by publishing a homelab.config.<ctid>.<entity>.<action> CloudEvent to the
// NATS event spine (CLAUDE.md rule 6). It is backed by a DEDICATED publish-only NATS nkey identity
// scoped to `publish: ["homelab.config.>"]` — so it is STRUCTURALLY unable to forge any event
// outside the homelab.config.> subtree, regardless of what the tool code does.
//
// Fail-soft: NatsConnectionOptions.FromEnvironment() uses neutral defaults, so the server boots
// even when the NATS env is absent (a publish then returns a structured error rather than
// crashing the backend behind the agentgateway's fail-closed initialize fan-out).
// ----------------------------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

// NKey + pinned-CA TLS connection settings, entirely env-driven (NATS_URL, NATS_NKEY,
// NATS_NKEY_SEED_PATH, NATS_TLS_CA_FILE). The seed is delivered to disk agent-free
// (register-secret Path D) and read back from NATS_NKEY_SEED_PATH; only the public key
// (NATS_NKEY) transits config.
var natsOpts = NatsConnectionOptions.FromEnvironment();
builder.Services.AddSingleton(natsOpts);

// CloudEvents producer URI for the emitted events. Identifies the EMITTER (this MCP), not the
// target CT — one tool records mutations for any ctid. Overridable via CLOUDEVENTS_SOURCE.
var source = Environment.GetEnvironmentVariable("CLOUDEVENTS_SOURCE") is { Length: > 0 } s
    ? s
    : "config-spine-mcp://ct121-mcp-gateway";

// One long-lived, lazily-connected publisher shared across tool calls.
builder.Services.AddSingleton<IConfigEventSink>(_ => new NatsConfigEventSink(natsOpts, source));

builder
    .AddSinapsiMcpServer("config-spine-mcp", "0.1.0")
    // Stateless transport strips a forwarded Mcp-Session-Id so a fronting proxy can't 400 it.
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<ConfigSpineTools>();

var app = builder.Build();
app.MapSinapsiMcp(envPrefix: "CONFIG_SPINE_MCP", defaultPort: 9216).Run();
