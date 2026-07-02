using Sinapsi.Mcp;
using StepCa.Mcp;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(StepCaOptions.FromEnvironment());
builder.Services.AddSingleton<StepCli>();

builder
    .AddSinapsiMcpServer("step-ca-mcp", "0.2.0")
    // Stateless transport strips a forwarded Mcp-Session-Id so a fronting proxy
    // cannot 400 an otherwise-valid request (e.g. a tools/list probe).
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<StepCaTools>();

var app = builder.Build();

app.MapSinapsiMcp(envPrefix: "STEP_CA_MCP", defaultPort: 9109).Run();
