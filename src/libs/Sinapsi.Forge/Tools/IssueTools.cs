using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Sinapsi.Forge.Tools;

/// <summary>Common issue, comment, label and milestone tools. Path-segment params (owner/repo)
/// and numeric ids are validated at the top of each tool via <see cref="SinapsiForgeValidation"/>;
/// upstream errors are scrubbed by <see cref="ForgeToolGuard"/>.</summary>
[McpServerToolType]
public sealed class IssueTools
{
    [McpServerTool(Name = "create_issue", Destructive = false)]
    [Description("Create an issue.")]
    public static Task<object> CreateIssue(IForgeClient forge, string owner, string repo,
        [Description("Issue title.")] string title,
        [Description("Issue body (markdown).")] string? body = null,
        [Description("Assignee logins.")] string[]? assignees = null,
        [Description("Label ids.")] long[]? labels = null,
        [Description("Milestone id.")] long? milestone = null,
        CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo)
                  ?? SinapsiForgeValidation.ValidateText(title, "title", 512),
            async () => await forge.CreateIssueAsync(owner, repo, new(title, body, assignees, labels, milestone), ct));

    [McpServerTool(Name = "get_issue", ReadOnly = true)]
    [Description("Get an issue by its number.")]
    public static Task<object> GetIssue(IForgeClient forge, string owner, string repo,
        [Description("Issue number.")] long number, CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo) ?? SinapsiForgeValidation.ValidatePositiveId(number, "number"),
            async () => await forge.GetIssueAsync(owner, repo, number, ct));

    [McpServerTool(Name = "list_issues", ReadOnly = true)]
    [Description("List issues, optionally filtered by state (open|closed|all) and comma-separated label names.")]
    public static Task<object> ListIssues(IForgeClient forge, string owner, string repo,
        string? state = null, string? labels = null, int limit = 30, CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo) ?? SinapsiForgeValidation.ValidateLimit(limit),
            async () => await forge.ListIssuesAsync(owner, repo, state, labels, limit, ct));

    [McpServerTool(Name = "update_issue", Destructive = false)]
    [Description("Update an issue (title/body/state/assignees/milestone). state = open|closed.")]
    public static Task<object> UpdateIssue(IForgeClient forge, string owner, string repo, long number,
        string? title = null, string? body = null, string? state = null, string[]? assignees = null, long? milestone = null,
        CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo) ?? SinapsiForgeValidation.ValidatePositiveId(number, "number"),
            async () => await forge.UpdateIssueAsync(owner, repo, number, new(title, body, state, assignees, milestone), ct));

    [McpServerTool(Name = "list_issue_comments", ReadOnly = true)]
    [Description("List comments on an issue.")]
    public static Task<object> ListIssueComments(IForgeClient forge, string owner, string repo, long number, CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo) ?? SinapsiForgeValidation.ValidatePositiveId(number, "number"),
            async () => await forge.ListIssueCommentsAsync(owner, repo, number, ct));

    [McpServerTool(Name = "create_issue_comment", Destructive = false)]
    [Description("Add a comment to an issue.")]
    public static Task<object> CreateIssueComment(IForgeClient forge, string owner, string repo, long number,
        [Description("Comment body (markdown).")] string body, CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo)
                  ?? SinapsiForgeValidation.ValidatePositiveId(number, "number")
                  ?? SinapsiForgeValidation.ValidateText(body, "body", 65536, allowNewlines: true),
            async () => await forge.CreateIssueCommentAsync(owner, repo, number, body, ct));

    [McpServerTool(Name = "edit_issue_comment", Destructive = false)]
    [Description("Edit an existing issue comment by its id.")]
    public static Task<object> EditIssueComment(IForgeClient forge, string owner, string repo,
        [Description("Comment id.")] long comment_id, [Description("New body.")] string body, CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo)
                  ?? SinapsiForgeValidation.ValidatePositiveId(comment_id, "comment_id")
                  ?? SinapsiForgeValidation.ValidateText(body, "body", 65536, allowNewlines: true),
            async () => await forge.EditIssueCommentAsync(owner, repo, comment_id, body, ct));

    [McpServerTool(Name = "delete_issue_comment", Destructive = false)]
    [Description("Delete an issue comment by its id.")]
    public static Task<object> DeleteIssueComment(IForgeClient forge, string owner, string repo, long comment_id, CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo) ?? SinapsiForgeValidation.ValidatePositiveId(comment_id, "comment_id"),
            async () =>
            {
                await forge.DeleteIssueCommentAsync(owner, repo, comment_id, ct);
                return new { deleted = comment_id };
            });

    [McpServerTool(Name = "list_repo_labels", ReadOnly = true)]
    [Description("List a repository's labels (id, name, color).")]
    public static Task<object> ListRepoLabels(IForgeClient forge, string owner, string repo, CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo),
            async () => await forge.ListRepoLabelsAsync(owner, repo, ct));

    [McpServerTool(Name = "add_issue_labels", Destructive = false)]
    [Description("Add labels (by id) to an issue.")]
    public static Task<object> AddIssueLabels(IForgeClient forge, string owner, string repo, long number,
        [Description("Label ids to add.")] long[] label_ids, CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo) ?? SinapsiForgeValidation.ValidatePositiveId(number, "number"),
            async () => await forge.AddIssueLabelsAsync(owner, repo, number, label_ids, ct));

    [McpServerTool(Name = "remove_issue_label", Destructive = false)]
    [Description("Remove a single label (by id) from an issue.")]
    public static Task<object> RemoveIssueLabel(IForgeClient forge, string owner, string repo, long number, long label_id, CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo)
                  ?? SinapsiForgeValidation.ValidatePositiveId(number, "number")
                  ?? SinapsiForgeValidation.ValidatePositiveId(label_id, "label_id"),
            async () =>
            {
                await forge.RemoveIssueLabelAsync(owner, repo, number, label_id, ct);
                return new { removed = label_id };
            });

    [McpServerTool(Name = "list_milestones", ReadOnly = true)]
    [Description("List a repository's milestones, optionally by state (open|closed|all).")]
    public static Task<object> ListMilestones(IForgeClient forge, string owner, string repo, string? state = null, CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo),
            async () => await forge.ListMilestonesAsync(owner, repo, state, ct));
}
