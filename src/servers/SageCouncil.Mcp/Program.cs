using Sinapsi.AgentJwt;
using Sinapsi.Mcp;
using SageCouncil.Mcp;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(CouncilOptions.FromEnvironment());
// Per-member machine-identity minting comes from the in-repo Sinapsi.AgentJwt
// library (env-driven: OIDC_ISSUER / OIDC_AUDIENCE_PROJECT_ID / AGENT_KEY_DIR /
// JWT_TTL_MIN). No vendored copy lives in this server.
builder.Services.AddSingleton(AgentJwtOptions.FromEnvironment());
builder.Services.AddSingleton<ConsultJobStore>();

// A consult runs for minutes. HttpClient's default 100s timeout would abort
// long research calls regardless of our CancellationToken (whichever is shorter
// wins), so disable it and let the per-call linked CTS (opt.Timeout) govern.
builder.Services.AddHttpClient<CouncilService>(c => c.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddHttpClient<AgentJwtMinter>(); // token mint is quick; default timeout is fine

var mcp = builder
    .AddSinapsiMcpServer("sage-council-mcp", "1.0.0")
    .WithHttpTransport()
    .WithTools<ConsultTool>();

var app = builder.Build();

app.MapSinapsiMcp(envPrefix: "SAGE_COUNCIL_MCP", defaultPort: 9212).Run();
