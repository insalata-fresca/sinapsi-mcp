using System.Net.Http.Headers;
using Sinapsi.Mcp;
using Zitadel.Mcp;
using Zitadel.Mcp.Api;
using Zitadel.Mcp.Tools;

var builder = WebApplication.CreateBuilder(args);
var cfg = ZitadelConfig.FromEnv();
builder.Services.AddSingleton(cfg);

// Typed HttpClient → ZitadelClient. The bearer token is held server-side; a fronting
// gateway, if any, terminates the caller's own auth — the host only ever holds the static
// service token.
builder.Services.AddHttpClient<ZitadelClient>(c =>
{
    c.BaseAddress = new Uri($"{cfg.BaseUrl}/");
    c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cfg.Token);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("sinapsi-zitadel-mcp/1.0");
});

builder
    .AddSinapsiMcpServer("zitadel-mcp", "1.0.0")
    // Stateless transport strips a forwarded Mcp-Session-Id so a fronting proxy can't 400 it.
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<UserTools>()
    .WithTools<ProjectTools>()
    .WithTools<OidcAppTools>()
    .WithTools<MachineUserTools>();

var app = builder.Build();
app.MapSinapsiMcp(envPrefix: "ZITADEL_MCP", defaultPort: cfg.Port).Run();
