using Metabase.Mcp;
using Metabase.Mcp.Api;
using Metabase.Mcp.Tools;
using Sinapsi.Mcp;

var builder = WebApplication.CreateBuilder(args);
var cfg = MetabaseConfig.FromEnv();
builder.Services.AddSingleton(cfg);

// Typed HttpClient → MetabaseClient. The API key is held server-side and sent as the
// X-API-KEY header; a fronting gateway, if any, terminates the caller's own auth — the
// host only ever holds the static Metabase key.
builder.Services.AddHttpClient<MetabaseClient>(c =>
{
    c.BaseAddress = new Uri($"{cfg.BaseUrl}/");
    c.DefaultRequestHeaders.Add("X-API-KEY", cfg.ApiKey);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("sinapsi-metabase-mcp/1.0");
});

builder
    .AddSinapsiMcpServer("metabase-mcp", "1.0.0")
    // Stateless transport strips a forwarded Mcp-Session-Id so a fronting proxy can't 400 it.
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<DatabaseTools>()
    .WithTools<TableTools>()
    .WithTools<CollectionTools>()
    .WithTools<CardTools>()
    .WithTools<DashboardTools>()
    .WithTools<UserTools>()
    .WithTools<ApiTools>();

var app = builder.Build();
app.MapSinapsiMcp(envPrefix: "METABASE_MCP", defaultPort: cfg.Port).Run();
