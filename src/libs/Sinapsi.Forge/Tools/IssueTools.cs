using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Sinapsi.Forge.Tools;

/// <summary>Common issue, comment, label and milestone tools.</summary>
[McpServerToolType]
public sealed class IssueTools
{
    [McpServerTool(Name = "create_issue", Destructive = false)]
    [Description("Create an issue.")]
    public static async Task<object> CreateIssue(IForgeClient forge, string owner, string repo,
        [Description("Issue title.")] string title,
        [Description("Issue body (markdown).")] string? body = null,
        [Description("Assignee logins.")] string[]? assignees = null,
        [Description("Label ids.")] long[]? labels = null,
        [Description("Milestone id.")] long? milestone = null,
        CancellationToken ct = default)
        => await forge.CreateIssueAsync(owner, repo, new(title, body, assignees, labels, milestone), ct);

    [McpServerTool(Name = "get_issue", ReadOnly = true)]
    [Description("Get an issue by its number.")]
    public static async Task<object> GetIssue(IForgeClient forge, string owner, string repo,
        [Description("Issue number.")] long number, CancellationToken ct = default)
        => await forge.GetIssueAsync(owner, repo, number, ct);

    [McpServerTool(Name = "list_issues", ReadOnly = true)]
    [Description("List issues, optionally filtered by state (open|closed|all) and comma-separated label names.")]
    public static async Task<object> ListIssues(IForgeClient forge, string owner, string repo,
        string? state = null, string? labels = null, int limit = 30, CancellationToken ct = default)
        => await forge.ListIssuesAsync(owner, repo, state, labels, limit, ct);

    [McpServerTool(Name = "update_issue", Destructive = false)]
    [Description("Update an issue (title/body/state/assignees/milestone). state = open|closed.")]
    public static async Task<object> UpdateIssue(IForgeClient forge, string owner, string repo, long number,
        string? title = null, string? body = null, string? state = null, string[]? assignees = null, long? milestone = null,
        CancellationToken ct = default)
        => await forge.UpdateIssueAsync(owner, repo, number, new(title, body, state, assignees, milestone), ct);

    [McpServerTool(Name = "list_issue_comments", ReadOnly = true)]
    [Description("List comments on an issue.")]
    public static async Task<object> ListIssueComments(IForgeClient forge, string owner, string repo, long number, CancellationToken ct = default)
        => await forge.ListIssueCommentsAsync(owner, repo, number, ct);

    [McpServerTool(Name = "create_issue_comment", Destructive = false)]
    [Description("Add a comment to an issue.")]
    public static async Task<object> CreateIssueComment(IForgeClient forge, string owner, string repo, long number,
        [Description("Comment body (markdown).")] string body, CancellationToken ct = default)
        => await forge.CreateIssueCommentAsync(owner, repo, number, body, ct);

    [McpServerTool(Name = "edit_issue_comment", Destructive = false)]
    [Description("Edit an existing issue comment by its id.")]
    public static async Task<object> EditIssueComment(IForgeClient forge, string owner, string repo,
        [Description("Comment id.")] long comment_id, [Description("New body.")] string body, CancellationToken ct = default)
        => await forge.EditIssueCommentAsync(owner, repo, comment_id, body, ct);

    [McpServerTool(Name = "delete_issue_comment", Destructive = false)]
    [Description("Delete an issue comment by its id.")]
    public static async Task<object> DeleteIssueComment(IForgeClient forge, string owner, string repo, long comment_id, CancellationToken ct = default)
    {
        await forge.DeleteIssueCommentAsync(owner, repo, comment_id, ct);
        return new { deleted = comment_id };
    }

    [McpServerTool(Name = "list_repo_labels", ReadOnly = true)]
    [Description("List a repository's labels (id, name, color).")]
    public static async Task<object> ListRepoLabels(IForgeClient forge, string owner, string repo, CancellationToken ct = default)
        => await forge.ListRepoLabelsAsync(owner, repo, ct);

    [McpServerTool(Name = "add_issue_labels", Destructive = false)]
    [Description("Add labels (by id) to an issue.")]
    public static async Task<object> AddIssueLabels(IForgeClient forge, string owner, string repo, long number,
        [Description("Label ids to add.")] long[] label_ids, CancellationToken ct = default)
        => await forge.AddIssueLabelsAsync(owner, repo, number, label_ids, ct);

    [McpServerTool(Name = "remove_issue_label", Destructive = false)]
    [Description("Remove a single label (by id) from an issue.")]
    public static async Task<object> RemoveIssueLabel(IForgeClient forge, string owner, string repo, long number, long label_id, CancellationToken ct = default)
    {
        await forge.RemoveIssueLabelAsync(owner, repo, number, label_id, ct);
        return new { removed = label_id };
    }

    [McpServerTool(Name = "list_milestones", ReadOnly = true)]
    [Description("List a repository's milestones, optionally by state (open|closed|all).")]
    public static async Task<object> ListMilestones(IForgeClient forge, string owner, string repo, string? state = null, CancellationToken ct = default)
        => await forge.ListMilestonesAsync(owner, repo, state, ct);
}
