using Infisical.Mcp;
using Sinapsi.Mcp;

var builder = WebApplication.CreateBuilder(args);

var infisicalOptions = InfisicalOptions.FromEnvironment();
builder.Services.AddSingleton(infisicalOptions);
// Bound every Infisical REST call with the configured hard timeout so a hung upstream
// cannot wedge a tool call (default 30 s; clamped fail-closed in InfisicalOptions).
builder.Services.AddHttpClient<InfisicalClient>(c =>
    c.Timeout = TimeSpan.FromMilliseconds(infisicalOptions.HttpTimeoutMs));

builder
    .AddSinapsiMcpServer("infisical-mcp", "0.1.0")
    // Stateless transport strips a forwarded Mcp-Session-Id so a fronting proxy can't 400 it.
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<InfisicalTools>();

var app = builder.Build();
app.MapSinapsiMcp(envPrefix: "INFISICAL_MCP", defaultPort: 9215).Run();
