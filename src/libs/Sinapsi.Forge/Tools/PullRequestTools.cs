using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Sinapsi.Forge.Tools;

/// <summary>Common pull-request tools, including the full review workflow. Path-segment params
/// (owner/repo) and the PR number are validated at the top of each tool via
/// <see cref="SinapsiForgeValidation"/>; upstream errors are scrubbed by <see cref="ForgeToolGuard"/>
/// (or, for merge, by <see cref="SinapsiForgeErrors.Sanitize"/> directly).</summary>
[McpServerToolType]
public sealed class PullRequestTools
{
    [McpServerTool(Name = "create_pull_request", Destructive = false)]
    [Description("Open a pull request from head into base. head/base are branch names (head may be 'owner:branch' for cross-repo).")]
    public static Task<object> CreatePullRequest(IForgeClient forge, string owner, string repo,
        [Description("PR title.")] string title,
        [Description("Source branch (head).")] string head,
        [Description("Target branch (base).")] string @base,
        [Description("PR body (markdown).")] string? body = null,
        CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo)
                  ?? SinapsiForgeValidation.ValidateText(title, "title", 512)
                  ?? SinapsiForgeValidation.ValidateRef(head, "head")
                  ?? SinapsiForgeValidation.ValidateRef(@base, "base"),
            async () => await forge.CreatePullRequestAsync(owner, repo, new(title, head, @base, body), ct));

    [McpServerTool(Name = "get_pull_request", ReadOnly = true)]
    [Description("Get a pull request by its number.")]
    public static Task<object> GetPullRequest(IForgeClient forge, string owner, string repo, long number, CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo) ?? SinapsiForgeValidation.ValidatePositiveId(number, "number"),
            async () => await forge.GetPullRequestAsync(owner, repo, number, ct));

    [McpServerTool(Name = "list_pull_requests", ReadOnly = true)]
    [Description("List pull requests, optionally by state (open|closed|all).")]
    public static Task<object> ListPullRequests(IForgeClient forge, string owner, string repo, string? state = null, int limit = 30, CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo) ?? SinapsiForgeValidation.ValidateLimit(limit),
            async () => await forge.ListPullRequestsAsync(owner, repo, state, limit, ct));

    [McpServerTool(Name = "update_pull_request", Destructive = false)]
    [Description("Update a pull request (title/body/state/base). state = open|closed.")]
    public static Task<object> UpdatePullRequest(IForgeClient forge, string owner, string repo, long number,
        string? title = null, string? body = null, string? state = null, string? @base = null, CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo)
                  ?? SinapsiForgeValidation.ValidatePositiveId(number, "number")
                  ?? SinapsiForgeValidation.ValidateRef(@base, "base", required: false),
            async () => await forge.UpdatePullRequestAsync(owner, repo, number, new(title, body, state, @base), ct));

    [McpServerTool(Name = "merge_pull_request", Destructive = true)]
    [Description("Merge a pull request. method = merge | rebase | rebase-merge | squash (default merge). " +
        "Confirms the merge actually landed (merged=true) rather than trusting the POST; on rejection returns the HTTP status + Forgejo body.")]
    public static async Task<object> MergePullRequest(IForgeClient forge, string owner, string repo, long number,
        string method = "merge", string? title = null, string? message = null, CancellationToken ct = default)
    {
        // Validate at the TOP before any HTTP call — same envelope shape as the guard.
        var reason = SinapsiForgeValidation.ValidateOwnerRepo(owner, repo) ?? SinapsiForgeValidation.ValidatePositiveId(number, "number");
        if (reason is not null)
            return new { ok = false, status = (int?)null, error = reason };
        try
        {
            // ForgeMergeResult: { number, merged, message } — merged=false carries the reason (e.g. raced).
            return await forge.MergePullRequestAsync(owner, repo, number, method, title, message, ct);
        }
        catch (Sinapsi.Forge.ForgeApiException ex)
        {
            // Surface the real rejection (branch protection, required checks, behind-base, conflict) as a
            // structured result. Status is the RAW verdict; the body is scrubbed of credential/key material.
            return new { number, merged = false, rejected = true, status = ex.Status, reason = SinapsiForgeErrors.Sanitize(ex.Message) };
        }
    }

    [McpServerTool(Name = "list_pull_request_files", ReadOnly = true)]
    [Description("List the files changed in a pull request.")]
    public static Task<object> ListPullRequestFiles(IForgeClient forge, string owner, string repo, long number, CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo) ?? SinapsiForgeValidation.ValidatePositiveId(number, "number"),
            async () => await forge.ListPullRequestFilesAsync(owner, repo, number, ct));

    [McpServerTool(Name = "get_pull_request_diff", ReadOnly = true)]
    [Description("Get the unified diff text of a pull request.")]
    public static Task<object> GetPullRequestDiff(IForgeClient forge, string owner, string repo, long number, CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo) ?? SinapsiForgeValidation.ValidatePositiveId(number, "number"),
            async () => new { number, diff = await forge.GetPullRequestDiffAsync(owner, repo, number, ct) });

    [McpServerTool(Name = "list_pull_reviews", ReadOnly = true)]
    [Description("List reviews on a pull request.")]
    public static Task<object> ListPullReviews(IForgeClient forge, string owner, string repo, long number, CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo) ?? SinapsiForgeValidation.ValidatePositiveId(number, "number"),
            async () => await forge.ListPullReviewsAsync(owner, repo, number, ct));

    [McpServerTool(Name = "create_pull_review", Destructive = false)]
    [Description("Create/submit a review. event = APPROVED | REQUEST_CHANGES | COMMENT | PENDING.")]
    public static Task<object> CreatePullReview(IForgeClient forge, string owner, string repo, long number,
        [Description("APPROVED | REQUEST_CHANGES | COMMENT | PENDING.")] string @event,
        [Description("Review body.")] string? body = null, CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo)
                  ?? SinapsiForgeValidation.ValidatePositiveId(number, "number")
                  ?? SinapsiForgeValidation.ValidateText(@event, "event", 32),
            async () => await forge.CreatePullReviewAsync(owner, repo, number, @event, body, ct));

    [McpServerTool(Name = "request_reviewers", Destructive = false)]
    [Description("Request (or with remove=true, un-request) reviewers on a pull request.")]
    public static Task<object> RequestReviewers(IForgeClient forge, string owner, string repo, long number,
        [Description("Reviewer logins.")] string[] reviewers,
        [Description("Set true to remove the review request instead of adding.")] bool remove = false,
        CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo) ?? SinapsiForgeValidation.ValidatePositiveId(number, "number"),
            async () =>
            {
                await forge.RequestReviewersAsync(owner, repo, number, reviewers, remove, ct);
                return new { number, reviewers, removed = remove };
            });
}
