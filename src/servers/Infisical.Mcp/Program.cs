using Infisical.Mcp;
using Sinapsi.Mcp;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(InfisicalOptions.FromEnvironment());
builder.Services.AddHttpClient<InfisicalClient>();

builder
    .AddSinapsiMcpServer("infisical-mcp", "0.1.0")
    .WithHttpTransport()
    .WithTools<InfisicalTools>();

var app = builder.Build();
app.MapSinapsiMcp(envPrefix: "INFISICAL_MCP", defaultPort: 9215).Run();
