using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Sinapsi.Forge.Tools;

/// <summary>Common commit tools.</summary>
[McpServerToolType]
public sealed class CommitTools
{
    [McpServerTool(Name = "list_commits", ReadOnly = true)]
    [Description("List commits on a repository, optionally starting from a ref (branch/tag/sha).")]
    public static async Task<object> ListCommits(IForgeClient forge, string owner, string repo,
        [Description("Start ref (branch/tag/sha); omit for the default branch.")] string? sha = null,
        [Description("Max results (default 30).")] int limit = 30, CancellationToken ct = default)
        => await forge.ListCommitsAsync(owner, repo, sha, limit, ct);

    [McpServerTool(Name = "get_commit", ReadOnly = true)]
    [Description("Get a single commit by sha.")]
    public static async Task<object> GetCommit(IForgeClient forge, string owner, string repo,
        [Description("Commit sha.")] string sha, CancellationToken ct = default)
        => await forge.GetCommitAsync(owner, repo, sha, ct);
}
