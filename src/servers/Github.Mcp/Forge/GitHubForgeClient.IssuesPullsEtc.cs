using System.Text.Json;
using Sinapsi.Forge;
using Sinapsi.Forge.Model;

namespace Github.Mcp.Forge;

public sealed partial class GitHubForgeClient
{
    // ── Issues ─────────────────────────────────────────────────────────────────
    public async Task<ForgeIssue> CreateIssueAsync(string owner, string repo, CreateIssueRequest req, CancellationToken ct = default)
    {
        var body = Prune(new Dictionary<string, object?>
        {
            ["title"] = req.Title, ["body"] = req.Body, ["assignees"] = req.Assignees, ["milestone"] = req.Milestone,
            // GitHub labels are by NAME on create; ids are not accepted. Pass through only if caller used names elsewhere.
        });
        return MapIssue((await SendJsonAsync(HttpMethod.Post, $"repos/{Esc(owner)}/{Esc(repo)}/issues", body, ct))!.Value);
    }

    public async Task<ForgeIssue> GetIssueAsync(string owner, string repo, long number, CancellationToken ct = default)
        => MapIssue(await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/issues/{number}", ct));

    public async Task<IReadOnlyList<ForgeIssue>> ListIssuesAsync(string owner, string repo, string? state, string? labels, int limit = 30, CancellationToken ct = default)
    {
        var qs = new List<string> { $"per_page={limit}" };
        if (!string.IsNullOrWhiteSpace(state)) qs.Add($"state={Esc(state!)}");
        if (!string.IsNullOrWhiteSpace(labels)) qs.Add($"labels={Esc(labels!)}");
        var doc = await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/issues?{string.Join('&', qs)}", ct);
        return doc.EnumerateArray().Select(MapIssue).ToList();
    }

    public async Task<ForgeIssue> UpdateIssueAsync(string owner, string repo, long number, UpdateIssueRequest req, CancellationToken ct = default)
    {
        var body = Prune(new Dictionary<string, object?>
        {
            ["title"] = req.Title, ["body"] = req.Body, ["state"] = req.State, ["assignees"] = req.Assignees, ["milestone"] = req.Milestone,
        });
        return MapIssue((await SendJsonAsync(HttpMethod.Patch, $"repos/{Esc(owner)}/{Esc(repo)}/issues/{number}", body, ct))!.Value);
    }

    public async Task<IReadOnlyList<ForgeComment>> ListIssueCommentsAsync(string owner, string repo, long number, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/issues/{number}/comments", ct);
        return doc.EnumerateArray().Select(MapComment).ToList();
    }

    public async Task<ForgeComment> CreateIssueCommentAsync(string owner, string repo, long number, string body, CancellationToken ct = default)
        => MapComment((await SendJsonAsync(HttpMethod.Post, $"repos/{Esc(owner)}/{Esc(repo)}/issues/{number}/comments", new { body }, ct))!.Value);

    public async Task<ForgeComment> EditIssueCommentAsync(string owner, string repo, long commentId, string body, CancellationToken ct = default)
        => MapComment((await SendJsonAsync(HttpMethod.Patch, $"repos/{Esc(owner)}/{Esc(repo)}/issues/comments/{commentId}", new { body }, ct))!.Value);

    public Task DeleteIssueCommentAsync(string owner, string repo, long commentId, CancellationToken ct = default)
        => SendJsonAsync(HttpMethod.Delete, $"repos/{Esc(owner)}/{Esc(repo)}/issues/comments/{commentId}", null, ct);

    public async Task<IReadOnlyList<ForgeLabel>> ListRepoLabelsAsync(string owner, string repo, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/labels", ct);
        return doc.EnumerateArray().Select(MapLabel).ToList();
    }

    // GitHub manages labels by NAME, not id — the id-based common ops are not supported here.
    public Task<IReadOnlyList<ForgeLabel>> AddIssueLabelsAsync(string owner, string repo, long number, IReadOnlyList<long> labelIds, CancellationToken ct = default)
        => throw new ForgeNotSupportedException("GitHub adds labels by name, not id. Use update_issue with label names, or the GitHub labels-by-name path.");

    public Task RemoveIssueLabelAsync(string owner, string repo, long number, long labelId, CancellationToken ct = default)
        => throw new ForgeNotSupportedException("GitHub removes labels by name, not id.");

    public async Task<IReadOnlyList<ForgeMilestone>> ListMilestonesAsync(string owner, string repo, string? state, CancellationToken ct = default)
    {
        var q = string.IsNullOrWhiteSpace(state) ? "" : $"?state={Esc(state!)}";
        var doc = await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/milestones{q}", ct);
        return doc.EnumerateArray().Select(MapMilestone).ToList();
    }

    // ── Pull requests ──────────────────────────────────────────────────────────
    public async Task<ForgePullRequest> CreatePullRequestAsync(string owner, string repo, CreatePullRequest req, CancellationToken ct = default)
    {
        var body = Prune(new Dictionary<string, object?>
        {
            ["title"] = req.Title, ["head"] = req.Head, ["base"] = req.Base, ["body"] = req.Body,
        });
        return MapPull((await SendJsonAsync(HttpMethod.Post, $"repos/{Esc(owner)}/{Esc(repo)}/pulls", body, ct))!.Value);
    }

    public async Task<ForgePullRequest> GetPullRequestAsync(string owner, string repo, long number, CancellationToken ct = default)
        => MapPull(await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/pulls/{number}", ct));

    public async Task<IReadOnlyList<ForgePullRequest>> ListPullRequestsAsync(string owner, string repo, string? state, int limit = 30, CancellationToken ct = default)
    {
        var q = string.IsNullOrWhiteSpace(state) ? $"?per_page={limit}" : $"?state={Esc(state!)}&per_page={limit}";
        var doc = await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/pulls{q}", ct);
        return doc.EnumerateArray().Select(MapPull).ToList();
    }

    public async Task<ForgePullRequest> UpdatePullRequestAsync(string owner, string repo, long number, UpdatePullRequest req, CancellationToken ct = default)
    {
        var body = Prune(new Dictionary<string, object?>
        {
            ["title"] = req.Title, ["body"] = req.Body, ["state"] = req.State, ["base"] = req.Base,
        });
        return MapPull((await SendJsonAsync(HttpMethod.Patch, $"repos/{Esc(owner)}/{Esc(repo)}/pulls/{number}", body, ct))!.Value);
    }

    public async Task<ForgeMergeResult> MergePullRequestAsync(string owner, string repo, long number, string method, string? title, string? message, CancellationToken ct = default)
    {
        var m = (method ?? "merge").ToLowerInvariant() switch
        {
            "squash" => "squash",
            "rebase" or "rebase-merge" => "rebase",
            _ => "merge",
        };
        var body = Prune(new Dictionary<string, object?> { ["merge_method"] = m, ["commit_title"] = title, ["commit_message"] = message });
        await SendJsonAsync(HttpMethod.Put, $"repos/{Esc(owner)}/{Esc(repo)}/pulls/{number}/merge", body, ct);
        return new ForgeMergeResult(number, true, null);
    }

    public async Task<IReadOnlyList<ForgePullFile>> ListPullRequestFilesAsync(string owner, string repo, long number, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/pulls/{number}/files", ct);
        return doc.EnumerateArray().Select(f => new ForgePullFile(
            Str(f, "filename") ?? "", Str(f, "status"), Num(f, "additions"), Num(f, "deletions"), Num(f, "changes"))).ToList();
    }

    public async Task<string> GetPullRequestDiffAsync(string owner, string repo, long number, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"repos/{Esc(owner)}/{Esc(repo)}/pulls/{number}");
        req.Headers.Accept.Clear();
        req.Headers.Accept.ParseAdd("application/vnd.github.diff");
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new ForgeApiException((int)resp.StatusCode, $"{(int)resp.StatusCode} fetching PR #{number} diff");
        return await resp.Content.ReadAsStringAsync(ct);
    }

    public async Task<IReadOnlyList<ForgeReview>> ListPullReviewsAsync(string owner, string repo, long number, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/pulls/{number}/reviews", ct);
        return doc.EnumerateArray().Select(MapReview).ToList();
    }

    public async Task<ForgeReview> CreatePullReviewAsync(string owner, string repo, long number, string @event, string? body, CancellationToken ct = default)
    {
        // GitHub uses APPROVE (Gitea: APPROVED). Normalise.
        var ev = @event?.ToUpperInvariant() switch
        {
            "APPROVED" or "APPROVE" => "APPROVE",
            "REQUEST_CHANGES" => "REQUEST_CHANGES",
            "PENDING" => (string?)null, // omit event → pending review
            _ => "COMMENT",
        };
        var payload = Prune(new Dictionary<string, object?> { ["event"] = ev, ["body"] = body });
        return MapReview((await SendJsonAsync(HttpMethod.Post, $"repos/{Esc(owner)}/{Esc(repo)}/pulls/{number}/reviews", payload, ct))!.Value);
    }

    public Task RequestReviewersAsync(string owner, string repo, long number, IReadOnlyList<string> reviewers, bool remove, CancellationToken ct = default)
        => SendJsonAsync(remove ? HttpMethod.Delete : HttpMethod.Post,
            $"repos/{Esc(owner)}/{Esc(repo)}/pulls/{number}/requested_reviewers", new { reviewers }, ct);

    // ── Releases & tags ──────────────────────────────────────────────────────
    public async Task<IReadOnlyList<ForgeRelease>> ListReleasesAsync(string owner, string repo, int limit = 30, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/releases?per_page={limit}", ct);
        return doc.EnumerateArray().Select(MapRelease).ToList();
    }

    public async Task<ForgeRelease> GetLatestReleaseAsync(string owner, string repo, CancellationToken ct = default)
        => MapRelease(await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/releases/latest", ct));

    public async Task<ForgeRelease> CreateReleaseAsync(string owner, string repo, CreateReleaseRequest req, CancellationToken ct = default)
    {
        var body = Prune(new Dictionary<string, object?>
        {
            ["tag_name"] = req.TagName, ["name"] = req.Name, ["body"] = req.Body,
            ["target_commitish"] = req.TargetCommitish, ["draft"] = req.Draft, ["prerelease"] = req.Prerelease,
        });
        return MapRelease((await SendJsonAsync(HttpMethod.Post, $"repos/{Esc(owner)}/{Esc(repo)}/releases", body, ct))!.Value);
    }

    public async Task<ForgeReleaseAsset> UploadReleaseAssetAsync(string owner, string repo, long releaseId, string name, string? contentBase64, string? sourcePath = null, string? sourceUrl = null, CancellationToken ct = default)
    {
        // GitHub asset uploads go to a different host.
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"https://uploads.github.com/repos/{Esc(owner)}/{Esc(repo)}/releases/{releaseId}/assets?name={Esc(name)}");
        var (content, dispose) = await ReleaseAssetContent.ResolveAsync(contentBase64, sourcePath, sourceUrl, ct);
        try
        {
            req.Content = content;
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(ct);
                throw new ForgeApiException((int)resp.StatusCode, $"{(int)resp.StatusCode} {resp.ReasonPhrase} uploading asset {name}: {errBody}");
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return MapAsset(doc.RootElement);
        }
        finally { dispose?.Dispose(); }
    }

    public async Task<IReadOnlyList<ForgeTag>> ListTagsAsync(string owner, string repo, int limit = 30, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/tags?per_page={limit}", ct);
        return doc.EnumerateArray().Select(t => new ForgeTag(
            Str(t, "name") ?? "", t.TryGetProperty("commit", out var c) ? Str(c, "sha") : null)).ToList();
    }

    public async Task<ForgeRelease> GetReleaseAsync(string owner, string repo, long releaseId, CancellationToken ct = default)
        => MapRelease(await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/releases/{releaseId}", ct));

    public async Task<ForgeRelease> GetReleaseByTagAsync(string owner, string repo, string tag, CancellationToken ct = default)
        => MapRelease(await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/releases/tags/{Esc(tag)}", ct));

    public async Task<ForgeRelease> EditReleaseAsync(string owner, string repo, long releaseId, EditReleaseRequest req, CancellationToken ct = default)
    {
        var body = Prune(new Dictionary<string, object?>
        {
            ["tag_name"] = req.TagName, ["target_commitish"] = req.TargetCommitish,
            ["name"] = req.Name, ["body"] = req.Body, ["draft"] = req.Draft, ["prerelease"] = req.Prerelease,
        });
        return MapRelease((await SendJsonAsync(HttpMethod.Patch, $"repos/{Esc(owner)}/{Esc(repo)}/releases/{releaseId}", body, ct))!.Value);
    }

    public Task DeleteReleaseAsync(string owner, string repo, long releaseId, CancellationToken ct = default)
        => SendJsonAsync(HttpMethod.Delete, $"repos/{Esc(owner)}/{Esc(repo)}/releases/{releaseId}", null, ct);

    public async Task<IReadOnlyList<ForgeReleaseAsset>> ListReleaseAssetsAsync(string owner, string repo, long releaseId, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/releases/{releaseId}/assets", ct);
        return doc.EnumerateArray().Select(MapAsset).ToList();
    }

    // GitHub asset edit/delete are keyed by asset id alone (no release id in the path).
    public async Task<ForgeReleaseAsset> EditReleaseAssetAsync(string owner, string repo, long releaseId, long assetId, string name, CancellationToken ct = default)
        => MapAsset((await SendJsonAsync(HttpMethod.Patch, $"repos/{Esc(owner)}/{Esc(repo)}/releases/assets/{assetId}", new { name }, ct))!.Value);

    public Task DeleteReleaseAssetAsync(string owner, string repo, long releaseId, long assetId, CancellationToken ct = default)
        => SendJsonAsync(HttpMethod.Delete, $"repos/{Esc(owner)}/{Esc(repo)}/releases/assets/{assetId}", null, ct);

    // ── Search ─────────────────────────────────────────────────────────────────
    public async Task<IReadOnlyList<ForgeIssue>> SearchIssuesAsync(string query, string type, int limit = 30, CancellationToken ct = default)
    {
        var qualifier = string.Equals(type, "pulls", StringComparison.OrdinalIgnoreCase) ? "is:pr" : "is:issue";
        var doc = await GetJsonAsync($"search/issues?q={Esc($"{query} {qualifier}")}&per_page={limit}", ct);
        var items = doc.TryGetProperty("items", out var it) ? it : doc;
        return items.EnumerateArray().Select(MapIssue).ToList();
    }

    // ── Orgs & teams ───────────────────────────────────────────────────────────
    public async Task<ForgeOrg> GetOrgAsync(string org, CancellationToken ct = default)
        => MapOrg(await GetJsonAsync($"orgs/{Esc(org)}", ct));

    public async Task<IReadOnlyList<ForgeOrg>> ListMyOrgsAsync(CancellationToken ct = default)
        => (await GetJsonAsync("user/orgs", ct)).EnumerateArray().Select(MapOrg).ToList();

    public async Task<IReadOnlyList<ForgeOrg>> ListUserOrgsAsync(string username, CancellationToken ct = default)
        => (await GetJsonAsync($"users/{Esc(username)}/orgs", ct)).EnumerateArray().Select(MapOrg).ToList();

    public async Task<IReadOnlyList<string>> ListOrgMembersAsync(string org, CancellationToken ct = default)
        => (await GetJsonAsync($"orgs/{Esc(org)}/members", ct)).EnumerateArray().Select(u => Str(u, "login") ?? "").Where(s => s.Length > 0).ToList();

    public async Task<bool> CheckOrgMembershipAsync(string org, string username, CancellationToken ct = default)
    {
        using var resp = await http.GetAsync($"orgs/{Esc(org)}/members/{Esc(username)}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NoContent || resp.IsSuccessStatusCode) return true;
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return false;
        throw new ForgeApiException((int)resp.StatusCode, $"{(int)resp.StatusCode} checking membership");
    }

    public async Task<IReadOnlyList<ForgeTeam>> ListOrgTeamsAsync(string org, CancellationToken ct = default)
        => (await GetJsonAsync($"orgs/{Esc(org)}/teams", ct)).EnumerateArray()
            .Select(t => new ForgeTeam(Num(t, "id") ?? 0, Str(t, "name") ?? "", Str(t, "permission"), Str(t, "description"))).ToList();

    private static ForgeOrg MapOrg(JsonElement o) => new(
        Login: Str(o, "login") ?? "", Id: Num(o, "id") ?? 0, FullName: Str(o, "name"),
        Description: Str(o, "description"), HtmlUrl: Str(o, "html_url"));

    // ── Notifications ──────────────────────────────────────────────────────────
    public async Task<IReadOnlyList<ForgeNotification>> ListNotificationsAsync(bool all, int limit, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync($"notifications?all={all.ToString().ToLowerInvariant()}&per_page={limit}", ct);
        return doc.EnumerateArray().Select(n => new ForgeNotification(
            Id: long.TryParse(Str(n, "id"), out var id) ? id : (Num(n, "id") ?? 0),
            Type: n.TryGetProperty("subject", out var s) ? Str(s, "type") : null,
            Title: n.TryGetProperty("subject", out var s2) ? Str(s2, "title") : null,
            State: null,
            Unread: Bool(n, "unread"),
            SubjectUrl: n.TryGetProperty("subject", out var s4) ? Str(s4, "url") : null)).ToList();
    }

    public Task MarkNotificationReadAsync(long id, CancellationToken ct = default)
        => SendJsonAsync(HttpMethod.Patch, $"notifications/threads/{id}", null, ct);

    public Task MarkAllNotificationsReadAsync(CancellationToken ct = default)
        => SendJsonAsync(HttpMethod.Put, "notifications", new { read = true }, ct);

    // ── Webhooks ─────────────────────────────────────────────────────────────
    public async Task<IReadOnlyList<ForgeWebhook>> ListWebhooksAsync(string owner, string repo, CancellationToken ct = default)
        => (await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/hooks", ct)).EnumerateArray().Select(MapHook).ToList();

    public async Task<ForgeWebhook> CreateWebhookAsync(string owner, string repo, string url, IReadOnlyList<string> events, string? secret, string contentType, CancellationToken ct = default)
    {
        var config = new Dictionary<string, object?> { ["url"] = url, ["content_type"] = string.IsNullOrWhiteSpace(contentType) ? "json" : contentType };
        if (!string.IsNullOrWhiteSpace(secret)) config["secret"] = secret;
        var body = new Dictionary<string, object?> { ["name"] = "web", ["active"] = true, ["events"] = events, ["config"] = config };
        return MapHook((await SendJsonAsync(HttpMethod.Post, $"repos/{Esc(owner)}/{Esc(repo)}/hooks", body, ct))!.Value);
    }

    public Task DeleteWebhookAsync(string owner, string repo, long id, CancellationToken ct = default)
        => SendJsonAsync(HttpMethod.Delete, $"repos/{Esc(owner)}/{Esc(repo)}/hooks/{id}", null, ct);

    private static ForgeWebhook MapHook(JsonElement h) => new(
        Id: Num(h, "id") ?? 0, Type: Str(h, "type") ?? Str(h, "name"), Active: Bool(h, "active"),
        Events: h.TryGetProperty("events", out var e) && e.ValueKind == JsonValueKind.Array
            ? e.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList() : [],
        Url: h.TryGetProperty("config", out var cfg) ? Str(cfg, "url") : null);

    // ── Time tracking — GitHub has no analogue ─────────────────────────────────
    public Task<IReadOnlyList<ForgeTrackedTime>> ListIssueTrackedTimesAsync(string owner, string repo, long number, CancellationToken ct = default)
        => throw new ForgeNotSupportedException("Time tracking is a Gitea/Forgejo feature; GitHub has no equivalent.");

    public Task<ForgeTrackedTime> AddIssueTimeAsync(string owner, string repo, long number, long seconds, CancellationToken ct = default)
        => throw new ForgeNotSupportedException("Time tracking is a Gitea/Forgejo feature; GitHub has no equivalent.");

    // ── shared mappers ─────────────────────────────────────────────────────────
    private static ForgeIssue MapIssue(JsonElement i) => new(
        Number: Num(i, "number") ?? 0, Title: Str(i, "title") ?? "", Body: Str(i, "body"), State: Str(i, "state") ?? "",
        AuthorLogin: i.TryGetProperty("user", out var u) ? Str(u, "login") : null,
        Assignees: i.TryGetProperty("assignees", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(x => Str(x, "login") ?? "").Where(s => s.Length > 0).ToList() : [],
        Labels: i.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Array
            ? l.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : (Str(x, "name") ?? "")).Where(s => s.Length > 0).ToList() : [],
        CommentCount: Num(i, "comments"), HtmlUrl: Str(i, "html_url"));

    private static ForgeComment MapComment(JsonElement c) => new(
        Id: Num(c, "id") ?? 0, Body: Str(c, "body"),
        AuthorLogin: c.TryGetProperty("user", out var u) ? Str(u, "login") : null,
        CreatedAt: c.TryGetProperty("created_at", out var dt) && dt.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(dt.GetString(), out var d) ? d : null,
        HtmlUrl: Str(c, "html_url"));

    private static ForgeLabel MapLabel(JsonElement l) => new(Num(l, "id") ?? 0, Str(l, "name") ?? "", Str(l, "color"), Str(l, "description"));
    private static ForgeMilestone MapMilestone(JsonElement m) => new(
        Num(m, "id") ?? 0, Str(m, "title") ?? "", Str(m, "state"), Str(m, "description"), Num(m, "open_issues"), Num(m, "closed_issues"));

    private static ForgePullRequest MapPull(JsonElement p) => new(
        Number: Num(p, "number") ?? 0, Title: Str(p, "title") ?? "", Body: Str(p, "body"), State: Str(p, "state") ?? "",
        Merged: Bool(p, "merged"), Mergeable: Bool(p, "mergeable"),
        HeadRef: p.TryGetProperty("head", out var h) ? Str(h, "ref") : null,
        BaseRef: p.TryGetProperty("base", out var b) ? Str(b, "ref") : null,
        AuthorLogin: p.TryGetProperty("user", out var u) ? Str(u, "login") : null, HtmlUrl: Str(p, "html_url"));

    private static ForgeReview MapReview(JsonElement r) => new(
        Id: Num(r, "id") ?? 0, State: Str(r, "state"), Body: Str(r, "body"),
        ReviewerLogin: r.TryGetProperty("user", out var u) ? Str(u, "login") : null,
        SubmittedAt: r.TryGetProperty("submitted_at", out var dt) && dt.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(dt.GetString(), out var d) ? d : null,
        HtmlUrl: Str(r, "html_url"));

    private static ForgeRelease MapRelease(JsonElement r) => new(
        Id: Num(r, "id") ?? 0, TagName: Str(r, "tag_name") ?? "", Name: Str(r, "name"), Body: Str(r, "body"),
        Draft: Bool(r, "draft") ?? false, Prerelease: Bool(r, "prerelease") ?? false, HtmlUrl: Str(r, "html_url"),
        Assets: r.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(MapAsset).ToList() : []);

    private static ForgeReleaseAsset MapAsset(JsonElement a) => new(
        Num(a, "id") ?? 0, Str(a, "name") ?? "", Num(a, "size"), Str(a, "browser_download_url") ?? Str(a, "download_url"));
}
