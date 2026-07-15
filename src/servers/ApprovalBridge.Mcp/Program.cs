using ApprovalBridge.Mcp;
using Sinapsi.Mcp;

var builder = WebApplication.CreateBuilder(args);

var options = ApprovalBridgeOptions.FromEnvironment();
builder.Services.AddSingleton(options);
// Bound every broker call with the configured hard timeout so a hung upstream cannot wedge a
// tool call (default 30 s; clamped fail-closed in ApprovalBridgeOptions).
builder.Services.AddHttpClient<ApprovalBridgeClient>(c =>
    c.Timeout = TimeSpan.FromMilliseconds(options.HttpTimeoutMs));

builder
    .AddSinapsiMcpServer("approval-bridge-mcp", "0.1.0")
    // Stateless transport strips a forwarded Mcp-Session-Id so a fronting proxy can't 400 it.
    .WithHttpTransport(o => o.Stateless = true)
    // E1.6: the ONLY tool this server exposes is the REQUEST path (approval_bridge_request).
    // There is no approve/reject tool — see ApprovalBridgeTools' doc comment for why that is
    // structural, not an oversight.
    .WithTools<ApprovalBridgeTools>();

var app = builder.Build();
app.MapSinapsiMcp(envPrefix: "APPROVAL_BRIDGE_MCP", defaultPort: 9219).Run();
