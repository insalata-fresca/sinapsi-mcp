using System.Net.Http.Headers;
using Github.Mcp.Forge;
using Sinapsi.Forge;
using Sinapsi.Forge.Tools;
using Sinapsi.Mcp;

var builder = WebApplication.CreateBuilder(args);

// PAT held server-side; a fronting gateway, if any, terminates the caller's own auth.
var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN")
            ?? Environment.GetEnvironmentVariable("GITHUB_PERSONAL_ACCESS_TOKEN")
            ?? throw new InvalidOperationException(
                "GITHUB_TOKEN (or GITHUB_PERSONAL_ACCESS_TOKEN) not set — inject the GitHub PAT at deploy, not baked in.");

// Fail-closed HTTP timeout: canonical GITHUB_HTTP_TIMEOUT_MS, validated to 1..600000 ms;
// a non-numeric / <=0 / out-of-range value throws on startup rather than running unbounded.
var httpTimeoutMs = ForgeClientOptions.ReadHttpTimeoutMs("GITHUB_HTTP_TIMEOUT_MS");

// Typed HttpClient → GitHubForgeClient registered as the shared IForgeClient.
builder.Services.AddHttpClient<IForgeClient, GitHubForgeClient>(c =>
{
    c.BaseAddress = new Uri("https://api.github.com/");
    c.Timeout = TimeSpan.FromMilliseconds(httpTimeoutMs);
    c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    c.DefaultRequestHeaders.UserAgent.ParseAdd("sinapsi-github-mcp/2.0");
    c.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
});

builder
    .AddSinapsiMcpServer("github-mcp", "2.0.0")
    .WithHttpTransport(o => o.Stateless = true)
    // The shared Sinapsi.Forge tool surface — identical names/semantics across all forges.
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
    // Optional on the Gitea hosts (FORGE_TOOLSETS can drop them for a forge that lacks the
    // endpoints); GitHub always exposes both, so they are unconditional here. Omitting them
    // was a registration bug, not a capability gap — GitHubForgeClient has implemented the
    // topics and actions methods all along, so `set_repo_topics` and friends simply never
    // reached the tool surface and repo topics had to be set by hand.
    .WithTools<TopicsTools>()
    .WithTools<ActionsTools>()
    // GitHub-only: the repository "Insights" surface (traffic / stats / community / SBOM /
    // forks / languages). Registered HERE ONLY — the Gitea-family hosts have no analogue.
    .WithTools<InsightsTools>();
    // No TimeTrackingTools — Gitea-only.

var app = builder.Build();
app.MapSinapsiMcp(envPrefix: "GITHUB_MCP", defaultPort: 9218).Run();
