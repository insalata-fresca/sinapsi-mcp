using System.Net.Http.Headers;
using Forge.Mcp;
using Sinapsi.Forge;
using Sinapsi.Forge.Gitea;
using Sinapsi.Forge.Tools;
using Sinapsi.Mcp;

var builder = WebApplication.CreateBuilder(args);
var cfg = ForgeConfig.FromEnv();
builder.Services.AddSingleton(cfg);

// Typed HttpClient → GiteaForgeClient registered as IForgeClient. The token is held
// server-side (Authorization: token <PAT>); a fronting gateway, if any, terminates the
// caller's own auth — the host only ever holds the static forge token.
builder.Services.AddHttpClient<IForgeClient, GiteaForgeClient>(c =>
{
    c.BaseAddress = new Uri(cfg.ApiBaseUrl);
    c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", cfg.Token);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("sinapsi-forge-mcp/1.0");
});

// One configurable host: the forgejo and codeberg deployments are this same binary with a
// different FORGE_NAME/FORGE_BASE_URL/FORGE_TOKEN, and FORGE_TOOLSETS to drop optional tool
// groups a given forge does not expose (e.g. a Codeberg instance without topics/actions).
var mcp = builder
    .AddSinapsiMcpServer($"{cfg.Name}-mcp", "1.0.0")
    // Stateless transport strips a forwarded Mcp-Session-Id so a fronting proxy can't 400 it.
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<UserTools>()
    .WithTools<RepoTools>()
    .WithTools<ContentTools>()
    .WithTools<BranchTools>()
    .WithTools<CommitTools>()
    .WithTools<IssueTools>()
    .WithTools<PullRequestTools>()
    .WithTools<ReleaseTools>()
    .WithTools<SearchTools>()
    .WithTools<OrgTools>()
    .WithTools<NotificationTools>()
    .WithTools<WebhookTools>()
    // Gitea-specialized — omitted by the GitHub host.
    .WithTools<TimeTrackingTools>();

// Optional groups, on by default, opt out per forge via FORGE_TOOLSETS (e.g. "-topics,-actions").
if (cfg.Toolsets.Topics) mcp.WithTools<TopicsTools>();
if (cfg.Toolsets.Actions) mcp.WithTools<ActionsTools>();

var app = builder.Build();
app.MapSinapsiMcp(envPrefix: "FORGE_MCP", defaultPort: cfg.Port).Run();
