using System.Text.Json;
using Sinapsi.Forge.Model;

namespace Sinapsi.Forge.Gitea;

public sealed partial class GiteaForgeClient
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
        // 201/204 No Content on success — the API returns no run handle by default.
        await SendJsonAsync(HttpMethod.Post,
            $"repos/{Esc(owner)}/{Esc(repo)}/actions/workflows/{Esc(workflow)}/dispatches", body, ct);
        return new ForgeWorkflowDispatchResult(true, workflow, gitRef,
            $"Dispatched {workflow} on {gitRef}. Use list_workflow_runs to track it.");
    }

    public async Task<IReadOnlyList<ForgeWorkflowRun>> ListWorkflowRunsAsync(
        string owner, string repo, string? workflow, int limit = 30, CancellationToken ct = default)
    {
        var q = $"?limit={limit}";
        if (!string.IsNullOrWhiteSpace(workflow)) q += $"&workflow_id={Esc(workflow!)}";
        var doc = await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/actions/runs{q}", ct);
        var runs = doc.TryGetProperty("workflow_runs", out var w) && w.ValueKind == JsonValueKind.Array ? w : doc;
        return runs.ValueKind == JsonValueKind.Array
            ? runs.EnumerateArray().Select(MapRun).ToList()
            : [];
    }

    private static ForgeWorkflowRun MapRun(JsonElement r) => new(
        Id: Num(r, "id") ?? 0,
        RunNumber: Num(r, "index_in_repo"),
        WorkflowId: Str(r, "workflow_id"),
        Title: Str(r, "title"),
        Status: Str(r, "status") ?? "",
        Conclusion: null,                                   // Gitea folds conclusion into status
        Event: Str(r, "trigger_event") ?? Str(r, "event"),
        HeadSha: Str(r, "commit_sha"),
        HeadBranch: Str(r, "prettyref"),
        HtmlUrl: Str(r, "html_url"),
        CreatedAt: Date(r, "created"),
        UpdatedAt: Date(r, "updated"));

    private static DateTimeOffset? Date(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), out var d) ? d : null;
}
