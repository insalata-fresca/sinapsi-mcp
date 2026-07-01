using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Sinapsi.Forge.Tools;

/// <summary>Common commit tools. Path-segment params are validated at the top of each tool
/// via <see cref="SinapsiForgeValidation"/>; upstream errors are scrubbed by <see cref="ForgeToolGuard"/>.</summary>
[McpServerToolType]
public sealed class CommitTools
{
    [McpServerTool(Name = "list_commits", ReadOnly = true)]
    [Description("List commits on a repository, optionally starting from a ref (branch/tag/sha).")]
    public static Task<object> ListCommits(IForgeClient forge, string owner, string repo,
        [Description("Start ref (branch/tag/sha); omit for the default branch.")] string? sha = null,
        [Description("Max results (default 30).")] int limit = 30, CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo)
                  ?? SinapsiForgeValidation.ValidateRef(sha, "sha", required: false)
                  ?? SinapsiForgeValidation.ValidateLimit(limit),
            async () => await forge.ListCommitsAsync(owner, repo, sha, limit, ct));

    [McpServerTool(Name = "get_commit", ReadOnly = true)]
    [Description("Get a single commit by sha.")]
    public static Task<object> GetCommit(IForgeClient forge, string owner, string repo,
        [Description("Commit sha.")] string sha, CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo) ?? SinapsiForgeValidation.ValidateRef(sha, "sha"),
            async () => await forge.GetCommitAsync(owner, repo, sha, ct));
}
