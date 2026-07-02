using Gemini.Mcp;
using Sinapsi.Mcp;

var builder = WebApplication.CreateBuilder(args);

// Immutable runtime config (paths, timeouts) resolved from the environment.
builder.Services.AddSingleton(GeminiConfig.FromEnvironment());

builder
    .AddSinapsiMcpServer("gemini-mcp", "0.2.0")
    // Stateless transport: the SDK otherwise requires an Mcp-Session-Id on every
    // non-initialize call. A fronting proxy or a direct curl caller may not supply
    // one, which would make tools/list and tools/call fail in stateless deployments.
    // Stateless mode (plus the inbound Mcp-Session-Id strip in MapSinapsiMcp) keeps
    // those callers working.
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<AskTool>()
    .WithTools<AskWithFilesTool>()
    .WithTools<ResearchTool>()
    .WithTools<SandboxTool>()
    .WithTools<ImageDescribeTool>()
    .WithTools<ImageGenerateTool>()
    .WithTools<SessionCreateTool>()
    .WithTools<SessionResumeTool>()
    .WithTools<SessionCloseTool>()
    .WithTools<GetStatusTool>();

var app = builder.Build();

// Ensure the runtime directories exist before serving.
var cfg = app.Services.GetRequiredService<GeminiConfig>();
Directory.CreateDirectory(cfg.SessionDir);
Directory.CreateDirectory(cfg.TaskDir);
Directory.CreateDirectory(cfg.OutputDir);

app.MapSinapsiMcp(envPrefix: "GEMINI_MCP", defaultPort: 9211).Run();
