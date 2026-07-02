using System.Text.Json;
using Sinapsi.Forge.Model;

namespace Sinapsi.Forge.Gitea;

public sealed partial class GiteaForgeClient
{
    // ── Search ─────────────────────────────────────────────────────────────────
    public async Task<IReadOnlyList<ForgeIssue>> SearchIssuesAsync(string query, string type, int limit = 30, CancellationToken ct = default)
    {
        // Gitea global issue search; type filters issues vs pulls.
        var t = string.Equals(type, "pulls", StringComparison.OrdinalIgnoreCase) ? "pulls" : "issues";
        var doc = await GetJsonAsync($"repos/issues/search?q={Esc(query)}&type={t}&limit={limit}", ct);
        var data = doc.ValueKind == JsonValueKind.Array ? doc : (doc.TryGetProperty("data", out var d) ? d : doc);
        return data.EnumerateArray().Select(MapIssue).ToList();
    }

    // ── Orgs & teams ───────────────────────────────────────────────────────────
    public async Task<ForgeOrg> GetOrgAsync(string org, CancellationToken ct = default)
        => MapOrg(await GetJsonAsync($"orgs/{Esc(org)}", ct));

    public async Task<IReadOnlyList<ForgeOrg>> ListMyOrgsAsync(CancellationToken ct = default)
    {
        var doc = await GetJsonAsync("user/orgs", ct);
        return doc.EnumerateArray().Select(MapOrg).ToList();
    }

    public async Task<IReadOnlyList<ForgeOrg>> ListUserOrgsAsync(string username, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync($"users/{Esc(username)}/orgs", ct);
        return doc.EnumerateArray().Select(MapOrg).ToList();
    }

    public async Task<IReadOnlyList<string>> ListOrgMembersAsync(string org, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync($"orgs/{Esc(org)}/members", ct);
        return doc.EnumerateArray().Select(u => Str(u, "login") ?? "").Where(s => s.Length > 0).ToList();
    }

    public async Task<bool> CheckOrgMembershipAsync(string org, string username, CancellationToken ct = default)
    {
        using var resp = await http.GetAsync($"orgs/{Esc(org)}/members/{Esc(username)}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NoContent || resp.IsSuccessStatusCode) return true;
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return false;
        throw new ForgeApiException((int)resp.StatusCode, $"{(int)resp.StatusCode} checking membership");
    }

    public async Task<IReadOnlyList<ForgeTeam>> ListOrgTeamsAsync(string org, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync($"orgs/{Esc(org)}/teams", ct);
        return doc.EnumerateArray().Select(t => new ForgeTeam(
            Num(t, "id") ?? 0, Str(t, "name") ?? "", Str(t, "permission"), Str(t, "description"))).ToList();
    }

    private static ForgeOrg MapOrg(JsonElement o) => new(
        Login: Str(o, "username") ?? Str(o, "login") ?? "",
        Id: Num(o, "id") ?? 0,
        FullName: Str(o, "full_name"),
        Description: Str(o, "description"),
        HtmlUrl: Str(o, "html_url"));

    // ── Notifications ──────────────────────────────────────────────────────────
    public async Task<IReadOnlyList<ForgeNotification>> ListNotificationsAsync(bool all, int limit, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync($"notifications?all={all.ToString().ToLowerInvariant()}&limit={limit}", ct);
        return doc.EnumerateArray().Select(n => new ForgeNotification(
            Id: Num(n, "id") ?? 0,
            Type: n.TryGetProperty("subject", out var s) ? Str(s, "type") : null,
            Title: n.TryGetProperty("subject", out var s2) ? Str(s2, "title") : null,
            State: n.TryGetProperty("subject", out var s3) ? Str(s3, "state") : null,
            Unread: Bool(n, "unread"),
            SubjectUrl: n.TryGetProperty("subject", out var s4) ? Str(s4, "url") : null)).ToList();
    }

    public Task MarkNotificationReadAsync(long id, CancellationToken ct = default)
        => SendJsonAsync(HttpMethod.Patch, $"notifications/threads/{id}", null, ct);

    public Task MarkAllNotificationsReadAsync(CancellationToken ct = default)
        => SendJsonAsync(HttpMethod.Put, "notifications", null, ct);

    // ── Webhooks ─────────────────────────────────────────────────────────────
    public async Task<IReadOnlyList<ForgeWebhook>> ListWebhooksAsync(string owner, string repo, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/hooks", ct);
        return doc.EnumerateArray().Select(MapHook).ToList();
    }

    public async Task<ForgeWebhook> CreateWebhookAsync(string owner, string repo, string url, IReadOnlyList<string> events, string? secret, string contentType, CancellationToken ct = default)
    {
        var config = new Dictionary<string, object?>
        {
            ["url"] = url,
            ["content_type"] = string.IsNullOrWhiteSpace(contentType) ? "json" : contentType,
        };
        if (!string.IsNullOrWhiteSpace(secret)) config["secret"] = secret;
        var body = new Dictionary<string, object?> { ["type"] = "gitea", ["active"] = true, ["events"] = events, ["config"] = config };
        var doc = await SendJsonAsync(HttpMethod.Post, $"repos/{Esc(owner)}/{Esc(repo)}/hooks", body, ct);
        return MapHook(doc!.Value);
    }

    public Task DeleteWebhookAsync(string owner, string repo, long id, CancellationToken ct = default)
        => SendJsonAsync(HttpMethod.Delete, $"repos/{Esc(owner)}/{Esc(repo)}/hooks/{id}", null, ct);

    private static ForgeWebhook MapHook(JsonElement h) => new(
        Id: Num(h, "id") ?? 0,
        Type: Str(h, "type"),
        Active: Bool(h, "active"),
        Events: h.TryGetProperty("events", out var e) && e.ValueKind == JsonValueKind.Array
            ? e.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList() : [],
        Url: h.TryGetProperty("config", out var cfg) ? Str(cfg, "url") : null);

    // ── Time tracking (Gitea-only) ─────────────────────────────────────────────
    public async Task<IReadOnlyList<ForgeTrackedTime>> ListIssueTrackedTimesAsync(string owner, string repo, long number, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/issues/{number}/times", ct);
        return doc.EnumerateArray().Select(MapTrackedTime).ToList();
    }

    public async Task<ForgeTrackedTime> AddIssueTimeAsync(string owner, string repo, long number, long seconds, CancellationToken ct = default)
    {
        var doc = await SendJsonAsync(HttpMethod.Post, $"repos/{Esc(owner)}/{Esc(repo)}/issues/{number}/times", new { time = seconds }, ct);
        return MapTrackedTime(doc!.Value);
    }

    private static ForgeTrackedTime MapTrackedTime(JsonElement t) => new(
        Id: Num(t, "id") ?? 0,
        IssueNumber: Num(t, "issue_id"),
        UserLogin: Str(t, "user_name"),
        Seconds: Num(t, "time") ?? 0,
        Created: t.TryGetProperty("created", out var dt) && dt.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(dt.GetString(), out var d) ? d : null);
}
