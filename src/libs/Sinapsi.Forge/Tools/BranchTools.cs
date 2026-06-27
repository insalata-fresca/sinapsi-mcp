using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Sinapsi.Forge.Tools;

/// <summary>Common branch tools.</summary>
[McpServerToolType]
public sealed class BranchTools
{
    [McpServerTool(Name = "list_branches", ReadOnly = true)]
    [Description("List branches in a repository.")]
    public static async Task<object> ListBranches(IForgeClient forge, string owner, string repo,
        [Description("Max results (default 50).")] int limit = 50, CancellationToken ct = default)
        => await forge.ListBranchesAsync(owner, repo, limit, ct);

    [McpServerTool(Name = "get_branch", ReadOnly = true)]
    [Description("Get a single branch (name + head commit sha + protection).")]
    public static async Task<object> GetBranch(IForgeClient forge, string owner, string repo,
        [Description("Branch name.")] string branch, CancellationToken ct = default)
        => await forge.GetBranchAsync(owner, repo, branch, ct);

    [McpServerTool(Name = "create_branch", Destructive = false)]
    [Description("Create a branch, optionally from a given source branch (default: the repo default branch).")]
    public static async Task<object> CreateBranch(IForgeClient forge, string owner, string repo,
        [Description("New branch name.")] string new_branch,
        [Description("Source branch to base it on; omit for the default branch.")] string? from_branch = null,
        CancellationToken ct = default)
        => await forge.CreateBranchAsync(owner, repo, new_branch, from_branch, ct);

    [McpServerTool(Name = "delete_branch", Destructive = false)]
    [Description("Delete a branch.")]
    public static async Task<object> DeleteBranch(IForgeClient forge, string owner, string repo,
        [Description("Branch name.")] string branch, CancellationToken ct = default)
    {
        await forge.DeleteBranchAsync(owner, repo, branch, ct);
        return new { deleted = branch };
    }
}
