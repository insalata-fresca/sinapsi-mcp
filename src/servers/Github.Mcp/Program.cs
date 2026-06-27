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

// Typed HttpClient → GitHubForgeClient registered as the shared IForgeClient.
builder.Services.AddHttpClient<IForgeClient, GitHubForgeClient>(c =>
{
    c.BaseAddress = new Uri("https://api.github.com/");
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
    .WithTools<WebhookTools>();
    // No TimeTrackingTools — Gitea-only.

var app = builder.Build();
app.MapSinapsiMcp(envPrefix: "GITHUB_MCP", defaultPort: 9218).Run();
