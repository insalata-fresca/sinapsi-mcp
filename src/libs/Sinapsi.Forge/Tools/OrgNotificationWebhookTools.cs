using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Sinapsi.Forge.Tools;

/// <summary>Common search tools (issues / pull requests).</summary>
[McpServerToolType]
public sealed class SearchTools
{
    [McpServerTool(Name = "search_issues", ReadOnly = true)]
    [Description("Search issues across repositories by keyword.")]
    public static async Task<object> SearchIssues(IForgeClient forge, string query, int limit = 30, CancellationToken ct = default)
        => await forge.SearchIssuesAsync(query, "issues", limit, ct);

    [McpServerTool(Name = "search_pull_requests", ReadOnly = true)]
    [Description("Search pull requests across repositories by keyword.")]
    public static async Task<object> SearchPullRequests(IForgeClient forge, string query, int limit = 30, CancellationToken ct = default)
        => await forge.SearchIssuesAsync(query, "pulls", limit, ct);
}

/// <summary>Common org + team tools.</summary>
[McpServerToolType]
public sealed class OrgTools
{
    [McpServerTool(Name = "get_org", ReadOnly = true)]
    [Description("Get an organization by name.")]
    public static async Task<object> GetOrg(IForgeClient forge, string org, CancellationToken ct = default)
        => await forge.GetOrgAsync(org, ct);

    [McpServerTool(Name = "list_my_orgs", ReadOnly = true)]
    [Description("List organizations the authenticated user belongs to.")]
    public static async Task<object> ListMyOrgs(IForgeClient forge, CancellationToken ct = default)
        => await forge.ListMyOrgsAsync(ct);

    [McpServerTool(Name = "list_user_orgs", ReadOnly = true)]
    [Description("List a user's organizations.")]
    public static async Task<object> ListUserOrgs(IForgeClient forge, string username, CancellationToken ct = default)
        => await forge.ListUserOrgsAsync(username, ct);

    [McpServerTool(Name = "list_org_members", ReadOnly = true)]
    [Description("List an organization's members (logins).")]
    public static async Task<object> ListOrgMembers(IForgeClient forge, string org, CancellationToken ct = default)
        => await forge.ListOrgMembersAsync(org, ct);

    [McpServerTool(Name = "check_org_membership", ReadOnly = true)]
    [Description("Check whether a user is a member of an organization.")]
    public static async Task<object> CheckOrgMembership(IForgeClient forge, string org, string username, CancellationToken ct = default)
        => new { org, username, member = await forge.CheckOrgMembershipAsync(org, username, ct) };

    [McpServerTool(Name = "list_org_teams", ReadOnly = true)]
    [Description("List an organization's teams.")]
    public static async Task<object> ListOrgTeams(IForgeClient forge, string org, CancellationToken ct = default)
        => await forge.ListOrgTeamsAsync(org, ct);
}

/// <summary>Common notification tools.</summary>
[McpServerToolType]
public sealed class NotificationTools
{
    [McpServerTool(Name = "list_notifications", ReadOnly = true)]
    [Description("List the authenticated user's notifications (all=true includes read).")]
    public static async Task<object> ListNotifications(IForgeClient forge, bool all = false, int limit = 30, CancellationToken ct = default)
        => await forge.ListNotificationsAsync(all, limit, ct);

    [McpServerTool(Name = "mark_notification_read", Destructive = false)]
    [Description("Mark a single notification thread as read.")]
    public static async Task<object> MarkNotificationRead(IForgeClient forge, long id, CancellationToken ct = default)
    {
        await forge.MarkNotificationReadAsync(id, ct);
        return new { marked = id };
    }

    [McpServerTool(Name = "mark_all_notifications_read", Destructive = false)]
    [Description("Mark all notifications as read.")]
    public static async Task<object> MarkAllNotificationsRead(IForgeClient forge, CancellationToken ct = default)
    {
        await forge.MarkAllNotificationsReadAsync(ct);
        return new { marked = "all" };
    }
}

/// <summary>Common webhook tools.</summary>
[McpServerToolType]
public sealed class WebhookTools
{
    [McpServerTool(Name = "list_webhooks", ReadOnly = true)]
    [Description("List a repository's webhooks.")]
    public static async Task<object> ListWebhooks(IForgeClient forge, string owner, string repo, CancellationToken ct = default)
        => await forge.ListWebhooksAsync(owner, repo, ct);

    [McpServerTool(Name = "create_webhook", Destructive = true)]
    [Description("Create a repository webhook (JSON payload) for the given events.")]
    public static async Task<object> CreateWebhook(IForgeClient forge, string owner, string repo,
        [Description("Payload URL.")] string url,
        [Description("Event names, e.g. push, pull_request.")] string[] events,
        [Description("Optional HMAC secret.")] string? secret = null,
        [Description("content_type: json | form (default json).")] string content_type = "json",
        CancellationToken ct = default)
        => await forge.CreateWebhookAsync(owner, repo, url, events, secret, content_type, ct);

    [McpServerTool(Name = "delete_webhook", Destructive = true)]
    [Description("Delete a repository webhook by id.")]
    public static async Task<object> DeleteWebhook(IForgeClient forge, string owner, string repo, long id, CancellationToken ct = default)
    {
        await forge.DeleteWebhookAsync(owner, repo, id, ct);
        return new { deleted = id };
    }
}
