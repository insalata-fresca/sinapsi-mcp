using System.Text.Json;
using Sinapsi.Forge.Model;

namespace Sinapsi.Forge.Gitea;

public sealed partial class GiteaForgeClient
{
    public async Task<ForgeIssue> CreateIssueAsync(string owner, string repo, CreateIssueRequest req, CancellationToken ct = default)
    {
        var body = Prune(new Dictionary<string, object?>
        {
            ["title"] = req.Title,
            ["body"] = req.Body,
            ["assignees"] = req.Assignees,
            ["labels"] = req.Labels,
            ["milestone"] = req.Milestone,
        });
        var doc = await SendJsonAsync(HttpMethod.Post, $"repos/{Esc(owner)}/{Esc(repo)}/issues", body, ct);
        return MapIssue(doc!.Value);
    }

    public async Task<ForgeIssue> GetIssueAsync(string owner, string repo, long number, CancellationToken ct = default)
        => MapIssue(await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/issues/{number}", ct));

    public async Task<IReadOnlyList<ForgeIssue>> ListIssuesAsync(string owner, string repo, string? state, string? labels, int limit = 30, CancellationToken ct = default)
    {
        var qs = new List<string> { $"limit={limit}" };
        if (!string.IsNullOrWhiteSpace(state)) qs.Add($"state={Esc(state!)}");
        if (!string.IsNullOrWhiteSpace(labels)) qs.Add($"labels={Esc(labels!)}");
        var doc = await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/issues?{string.Join('&', qs)}", ct);
        return doc.EnumerateArray().Select(MapIssue).ToList();
    }

    public async Task<ForgeIssue> UpdateIssueAsync(string owner, string repo, long number, UpdateIssueRequest req, CancellationToken ct = default)
    {
        var body = Prune(new Dictionary<string, object?>
        {
            ["title"] = req.Title,
            ["body"] = req.Body,
            ["state"] = req.State,
            ["assignees"] = req.Assignees,
            ["milestone"] = req.Milestone,
        });
        var doc = await SendJsonAsync(HttpMethod.Patch, $"repos/{Esc(owner)}/{Esc(repo)}/issues/{number}", body, ct);
        return MapIssue(doc!.Value);
    }

    public async Task<IReadOnlyList<ForgeComment>> ListIssueCommentsAsync(string owner, string repo, long number, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/issues/{number}/comments", ct);
        return doc.EnumerateArray().Select(MapComment).ToList();
    }

    public async Task<ForgeComment> CreateIssueCommentAsync(string owner, string repo, long number, string body, CancellationToken ct = default)
    {
        var doc = await SendJsonAsync(HttpMethod.Post, $"repos/{Esc(owner)}/{Esc(repo)}/issues/{number}/comments", new { body }, ct);
        return MapComment(doc!.Value);
    }

    public async Task<ForgeComment> EditIssueCommentAsync(string owner, string repo, long commentId, string body, CancellationToken ct = default)
    {
        var doc = await SendJsonAsync(HttpMethod.Patch, $"repos/{Esc(owner)}/{Esc(repo)}/issues/comments/{commentId}", new { body }, ct);
        return MapComment(doc!.Value);
    }

    public Task DeleteIssueCommentAsync(string owner, string repo, long commentId, CancellationToken ct = default)
        => SendJsonAsync(HttpMethod.Delete, $"repos/{Esc(owner)}/{Esc(repo)}/issues/comments/{commentId}", null, ct);

    public async Task<IReadOnlyList<ForgeLabel>> ListRepoLabelsAsync(string owner, string repo, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/labels", ct);
        return doc.EnumerateArray().Select(MapLabel).ToList();
    }

    public async Task<IReadOnlyList<ForgeLabel>> AddIssueLabelsAsync(string owner, string repo, long number, IReadOnlyList<long> labelIds, CancellationToken ct = default)
    {
        var doc = await SendJsonAsync(HttpMethod.Post, $"repos/{Esc(owner)}/{Esc(repo)}/issues/{number}/labels", new { labels = labelIds }, ct);
        return doc!.Value.EnumerateArray().Select(MapLabel).ToList();
    }

    public Task RemoveIssueLabelAsync(string owner, string repo, long number, long labelId, CancellationToken ct = default)
        => SendJsonAsync(HttpMethod.Delete, $"repos/{Esc(owner)}/{Esc(repo)}/issues/{number}/labels/{labelId}", null, ct);

    public async Task<IReadOnlyList<ForgeMilestone>> ListMilestonesAsync(string owner, string repo, string? state, CancellationToken ct = default)
    {
        var q = string.IsNullOrWhiteSpace(state) ? "" : $"?state={Esc(state!)}";
        var doc = await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/milestones{q}", ct);
        return doc.EnumerateArray().Select(MapMilestone).ToList();
    }

    // ── mapping ────────────────────────────────────────────────────────────────
    private static ForgeIssue MapIssue(JsonElement i) => new(
        Number: Num(i, "number") ?? 0,
        Title: Str(i, "title") ?? "",
        Body: Str(i, "body"),
        State: Str(i, "state") ?? "",
        AuthorLogin: i.TryGetProperty("user", out var u) ? Str(u, "login") : null,
        Assignees: i.TryGetProperty("assignees", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(x => Str(x, "login") ?? "").Where(s => s.Length > 0).ToList() : [],
        Labels: i.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Array
            ? l.EnumerateArray().Select(x => Str(x, "name") ?? "").Where(s => s.Length > 0).ToList() : [],
        CommentCount: Num(i, "comments"),
        HtmlUrl: Str(i, "html_url"));

    private static ForgeComment MapComment(JsonElement c) => new(
        Id: Num(c, "id") ?? 0,
        Body: Str(c, "body"),
        AuthorLogin: c.TryGetProperty("user", out var u) ? Str(u, "login") : null,
        CreatedAt: c.TryGetProperty("created_at", out var dt) && dt.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(dt.GetString(), out var d) ? d : null,
        HtmlUrl: Str(c, "html_url"));

    private static ForgeLabel MapLabel(JsonElement l) => new(
        Id: Num(l, "id") ?? 0, Name: Str(l, "name") ?? "", Color: Str(l, "color"), Description: Str(l, "description"));

    private static ForgeMilestone MapMilestone(JsonElement m) => new(
        Id: Num(m, "id") ?? 0, Title: Str(m, "title") ?? "", State: Str(m, "state"), Description: Str(m, "description"),
        OpenIssues: Num(m, "open_issues"), ClosedIssues: Num(m, "closed_issues"));
}
