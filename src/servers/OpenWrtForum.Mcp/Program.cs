using OpenWrtForum.Mcp;
using Sinapsi.Mcp;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(DiscourseOptions.FromEnvironment());
builder.Services.AddSingleton<DiscourseClient>();

builder
    .AddSinapsiMcpServer("openwrt-forum-mcp", "0.1.0")
    // Stateless transport strips a forwarded Mcp-Session-Id header so a fronting
    // proxy that aggregates tools/list can't 400 an otherwise-valid request.
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<ForumTools>();

var app = builder.Build();

app.MapSinapsiMcp(envPrefix: "DISCOURSE_MCP", defaultPort: 9207).Run();
