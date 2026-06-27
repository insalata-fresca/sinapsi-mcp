using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Sinapsi.Forge.Tools;

/// <summary>
/// Gitea-specialized time-tracking tools (no GitHub analogue). Registered only by the
/// forge-mcp host; the GitHub host omits this class.
/// </summary>
[McpServerToolType]
public sealed class TimeTrackingTools
{
    [McpServerTool(Name = "list_issue_tracked_times", ReadOnly = true)]
    [Description("List tracked time entries on an issue.")]
    public static async Task<object> ListIssueTrackedTimes(IForgeClient forge, string owner, string repo, long number, CancellationToken ct = default)
        => await forge.ListIssueTrackedTimesAsync(owner, repo, number, ct);

    [McpServerTool(Name = "add_issue_time", Destructive = false)]
    [Description("Add a tracked-time entry (in seconds) to an issue.")]
    public static async Task<object> AddIssueTime(IForgeClient forge, string owner, string repo, long number,
        [Description("Seconds to add.")] long seconds, CancellationToken ct = default)
        => await forge.AddIssueTimeAsync(owner, repo, number, seconds, ct);
}
