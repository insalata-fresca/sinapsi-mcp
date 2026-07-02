using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Sinapsi.Forge.Tools;

/// <summary>
/// Gitea-specialized time-tracking tools (no GitHub analogue). Registered only by the
/// forge-mcp host; the GitHub host omits this class. Path-segment params (owner/repo)
/// and the issue number are validated at the top of each tool via
/// <see cref="SinapsiForgeValidation"/>; upstream errors are scrubbed by
/// <see cref="ForgeToolGuard"/>.
/// </summary>
[McpServerToolType]
public sealed class TimeTrackingTools
{
    [McpServerTool(Name = "list_issue_tracked_times", ReadOnly = true)]
    [Description("List tracked time entries on an issue.")]
    public static Task<object> ListIssueTrackedTimes(IForgeClient forge, string owner, string repo, long number, CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo) ?? SinapsiForgeValidation.ValidatePositiveId(number, "number"),
            async () => await forge.ListIssueTrackedTimesAsync(owner, repo, number, ct));

    [McpServerTool(Name = "add_issue_time", Destructive = false)]
    [Description("Add a tracked-time entry (in seconds) to an issue.")]
    public static Task<object> AddIssueTime(IForgeClient forge, string owner, string repo, long number,
        [Description("Seconds to add.")] long seconds, CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo) ?? SinapsiForgeValidation.ValidatePositiveId(number, "number"),
            async () => await forge.AddIssueTimeAsync(owner, repo, number, seconds, ct));
}
