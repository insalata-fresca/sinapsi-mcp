using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Sinapsi.Forge.Tools;

/// <summary>
/// CI workflow tools — dispatch (trigger / re-fire) a workflow and list its runs.
/// Maps to the Gitea/Forgejo + GitHub Actions API. Lets an agent re-run a build
/// without pushing a throwaway commit (the failure mode forge-mcp #590 fixed).
/// Path-segment params are validated at the top of each tool via
/// <see cref="SinapsiForgeValidation"/>; upstream errors are scrubbed by
/// <see cref="ForgeToolGuard"/>.
/// </summary>
[McpServerToolType]
public sealed class ActionsTools
{
    [McpServerTool(Name = "dispatch_workflow", Destructive = false)]
    [Description("Trigger (or re-run) a CI workflow via workflow_dispatch — no throwaway commit needed. The workflow file must declare an `on: workflow_dispatch` trigger.")]
    public static Task<object> DispatchWorkflow(IForgeClient forge, string owner, string repo,
        [Description("Workflow file name, e.g. \"build.yml\" (the file under .forgejo/.github workflows).")] string workflow,
        [Description("Git ref (branch or tag) to run on.")] string @ref = "main",
        [Description("Optional workflow_dispatch inputs, as a string→string map.")] IReadOnlyDictionary<string, string>? inputs = null,
        CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo)
                  ?? SinapsiForgeValidation.ValidateText(workflow, "workflow", 255)
                  ?? SinapsiForgeValidation.ValidateRef(@ref, "ref"),
            async () => await forge.DispatchWorkflowAsync(owner, repo, workflow, @ref, inputs, ct));

    [McpServerTool(Name = "list_workflow_runs", ReadOnly = true)]
    [Description("List recent CI workflow runs (status, conclusion, html_url, commit), newest first. Optionally filter to a single workflow file.")]
    public static Task<object> ListWorkflowRuns(IForgeClient forge, string owner, string repo,
        [Description("Optional workflow file name to filter by, e.g. \"build.yml\".")] string? workflow = null,
        int limit = 30, CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo)
                  ?? SinapsiForgeValidation.ValidateText(workflow, "workflow", 255, required: false)
                  ?? SinapsiForgeValidation.ValidateLimit(limit),
            async () => await forge.ListWorkflowRunsAsync(owner, repo, workflow, limit, ct));
}
