using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Sinapsi.Forge.Tools;

/// <summary>Common repository tools.</summary>
[McpServerToolType]
public sealed class RepoTools
{
    [McpServerTool(Name = "get_repo", ReadOnly = true)]
    [Description("Get a repository by owner/name.")]
    public static Task<object> GetRepo(IForgeClient forge, string owner, string repo, CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(async () => await forge.GetRepoAsync(owner, repo, ct));

    [McpServerTool(Name = "list_my_repos", ReadOnly = true)]
    [Description("List repositories owned by / accessible to the authenticated user.")]
    public static async Task<object> ListMyRepos(IForgeClient forge,
        [Description("Max results (default 50).")] int limit = 50, CancellationToken ct = default)
        => await forge.ListMyReposAsync(limit, ct);

    [McpServerTool(Name = "search_repos", ReadOnly = true)]
    [Description("Search repositories by keyword.")]
    public static async Task<object> SearchRepos(IForgeClient forge,
        [Description("Search query.")] string query,
        [Description("Max results (default 30).")] int limit = 30, CancellationToken ct = default)
        => await forge.SearchReposAsync(query, limit, ct);

    [McpServerTool(Name = "create_repo", Destructive = false)]
    [Description("Create a repository. Omit `owner` to create under the authenticated user; set it to an ORG name to create in that org.")]
    public static async Task<object> CreateRepo(IForgeClient forge,
        [Description("Repository name.")] string name,
        [Description("Owner ORG; omit for the authenticated user.")] string? owner = null,
        [Description("Description.")] string? description = null,
        [Description("Private (default true).")] bool @private = true,
        [Description("Auto-initialise with a README/commit (default false).")] bool auto_init = false,
        [Description("Default branch name.")] string? default_branch = null,
        CancellationToken ct = default)
        => await forge.CreateRepoAsync(new(name, owner, description, @private, auto_init, default_branch), ct);

    [McpServerTool(Name = "fork_repo", Destructive = false)]
    [Description("Fork a repository, optionally into an organization.")]
    public static async Task<object> ForkRepo(IForgeClient forge, string owner, string repo,
        [Description("Target org for the fork; omit to fork to the authenticated user.")] string? organization = null,
        CancellationToken ct = default)
        => await forge.ForkRepoAsync(owner, repo, organization, ct);

    [McpServerTool(Name = "edit_repo", Destructive = true)]
    [Description("Edit repository settings (description, visibility, default branch, issues/wiki, releases unit, archived).")]
    public static Task<object> EditRepo(IForgeClient forge, string owner, string repo,
        string? description = null, bool? @private = null, string? default_branch = null,
        bool? has_issues = null, bool? has_wiki = null, bool? archived = null,
        [Description("Enable/disable the Releases unit. Set true to fix release endpoints 404ing on a repo with has_releases:false.")] bool? has_releases = null,
        CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(async () => await forge.EditRepoAsync(owner, repo, new(description, @private, default_branch, has_issues, has_wiki, archived, has_releases), ct));

    [McpServerTool(Name = "delete_repo", Destructive = true)]
    [Description("Delete a repository. Irreversible.")]
    public static async Task<object> DeleteRepo(IForgeClient forge, string owner, string repo, CancellationToken ct = default)
    {
        await forge.DeleteRepoAsync(owner, repo, ct);
        return new { deleted = $"{owner}/{repo}" };
    }
}
