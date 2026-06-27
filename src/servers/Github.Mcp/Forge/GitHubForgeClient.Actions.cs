using System.Text.Json;
using Sinapsi.Forge.Model;

namespace Github.Mcp.Forge;

public sealed partial class GitHubForgeClient
{
    public async Task<ForgeWorkflowDispatchResult> DispatchWorkflowAsync(
        string owner, string repo, string workflow, string gitRef,
        IReadOnlyDictionary<string, string>? inputs, CancellationToken ct = default)
    {
        var body = Prune(new Dictionary<string, object?>
        {
            ["ref"] = gitRef,
            ["inputs"] = inputs is { Count: > 0 } ? inputs : null,
        });
        // 204 No Content on success.
        await SendJsonAsync(HttpMethod.Post,
            $"repos/{Esc(owner)}/{Esc(repo)}/actions/workflows/{Esc(workflow)}/dispatches", body, ct);
        return new ForgeWorkflowDispatchResult(true, workflow, gitRef,
            $"Dispatched {workflow} on {gitRef}. Use list_workflow_runs to track it.");
    }

    public async Task<IReadOnlyList<ForgeWorkflowRun>> ListWorkflowRunsAsync(
        string owner, string repo, string? workflow, int limit = 30, CancellationToken ct = default)
    {
        // GitHub filters by workflow via a dedicated path; unfiltered uses /actions/runs.
        var path = string.IsNullOrWhiteSpace(workflow)
            ? $"repos/{Esc(owner)}/{Esc(repo)}/actions/runs?per_page={limit}"
            : $"repos/{Esc(owner)}/{Esc(repo)}/actions/workflows/{Esc(workflow!)}/runs?per_page={limit}";
        var doc = await GetJsonAsync(path, ct);
        var runs = doc.TryGetProperty("workflow_runs", out var w) && w.ValueKind == JsonValueKind.Array ? w : doc;
        return runs.ValueKind == JsonValueKind.Array
            ? runs.EnumerateArray().Select(MapRun).ToList()
            : [];
    }

    private static ForgeWorkflowRun MapRun(JsonElement r) => new(
        Id: Num(r, "id") ?? 0,
        RunNumber: Num(r, "run_number"),
        WorkflowId: Str(r, "name"),
        Title: Str(r, "display_title") ?? Str(r, "name"),
        Status: Str(r, "status") ?? "",
        Conclusion: Str(r, "conclusion"),
        Event: Str(r, "event"),
        HeadSha: Str(r, "head_sha"),
        HeadBranch: Str(r, "head_branch"),
        HtmlUrl: Str(r, "html_url"),
        CreatedAt: Date(r, "created_at"),
        UpdatedAt: Date(r, "updated_at"));

    private static DateTimeOffset? Date(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), out var d) ? d : null;
}
