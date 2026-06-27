using System.Text.Json;
using Sinapsi.Forge.Model;

namespace Sinapsi.Forge.Gitea;

public sealed partial class GiteaForgeClient
{
    public async Task<ForgePullRequest> CreatePullRequestAsync(string owner, string repo, CreatePullRequest req, CancellationToken ct = default)
    {
        var body = Prune(new Dictionary<string, object?>
        {
            ["title"] = req.Title,
            ["head"] = req.Head,
            ["base"] = req.Base,
            ["body"] = req.Body,
            ["assignees"] = req.Assignees,
        });
        var doc = await SendJsonAsync(HttpMethod.Post, $"repos/{Esc(owner)}/{Esc(repo)}/pulls", body, ct);
        return MapPull(doc!.Value);
    }

    public async Task<ForgePullRequest> GetPullRequestAsync(string owner, string repo, long number, CancellationToken ct = default)
        => MapPull(await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/pulls/{number}", ct));

    public async Task<IReadOnlyList<ForgePullRequest>> ListPullRequestsAsync(string owner, string repo, string? state, int limit = 30, CancellationToken ct = default)
    {
        var q = string.IsNullOrWhiteSpace(state) ? $"?limit={limit}" : $"?state={Esc(state!)}&limit={limit}";
        var doc = await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/pulls{q}", ct);
        return doc.EnumerateArray().Select(MapPull).ToList();
    }

    public async Task<ForgePullRequest> UpdatePullRequestAsync(string owner, string repo, long number, UpdatePullRequest req, CancellationToken ct = default)
    {
        var body = Prune(new Dictionary<string, object?>
        {
            ["title"] = req.Title,
            ["body"] = req.Body,
            ["state"] = req.State,
            ["base"] = req.Base,
        });
        var doc = await SendJsonAsync(HttpMethod.Patch, $"repos/{Esc(owner)}/{Esc(repo)}/pulls/{number}", body, ct);
        return MapPull(doc!.Value);
    }

    public async Task<ForgeMergeResult> MergePullRequestAsync(string owner, string repo, long number, string method, string? title, string? message, CancellationToken ct = default)
    {
        // Gitea wants the merge style in `Do`: merge | rebase | rebase-merge | squash.
        var body = Prune(new Dictionary<string, object?>
        {
            ["Do"] = string.IsNullOrWhiteSpace(method) ? "merge" : method,
            ["MergeTitleField"] = title,
            ["MergeMessageField"] = message,
        });
        var path = $"repos/{Esc(owner)}/{Esc(repo)}/pulls/{number}/merge";

        // (c) One bounded retry — only on a transient failure (5xx / timeout / connection).
        //     4xx are real rejections (branch protection, behind-base, conflict) — never retried.
        var attempt = 0;
        while (true)
        {
            try
            {
                await SendJsonAsync(HttpMethod.Post, path, body, ct);
                break;
            }
            catch (ForgeApiException ex) when (ex.Status >= 500 && attempt == 0)
            {
                attempt++; // transient server error — retry once.
            }
            catch (ForgeApiException ex)
            {
                // (b) Surface the real rejection: status + Forgejo body (already in ex.Message).
                throw new ForgeApiException(ex.Status, $"merge rejected: HTTP {ex.Status} — {ex.Message}");
            }
            catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested && attempt == 0)
            {
                attempt++; // transient transport error / timeout — retry once.
            }
        }

        // (a) Confirm the merge actually landed — do not trust the POST's 2xx (it can silently
        //     no-op when main moved under us, the loser of a merge race).
        var pr = await GetPullRequestAsync(owner, repo, number, ct);
        var merged = pr.Merged == true;
        var detail = merged
            ? null
            : "merge POST returned 2xx but PR still open — likely raced; re-check/rebase";
        return new ForgeMergeResult(number, merged, detail);
    }

    public async Task<IReadOnlyList<ForgePullFile>> ListPullRequestFilesAsync(string owner, string repo, long number, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/pulls/{number}/files", ct);
        return doc.EnumerateArray().Select(f => new ForgePullFile(
            Str(f, "filename") ?? "", Str(f, "status"), Num(f, "additions"), Num(f, "deletions"), Num(f, "changes"))).ToList();
    }

    public async Task<string> GetPullRequestDiffAsync(string owner, string repo, long number, CancellationToken ct = default)
    {
        using var resp = await http.GetAsync($"repos/{Esc(owner)}/{Esc(repo)}/pulls/{number}.diff", ct);
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
        // event: APPROVED | REQUEST_CHANGES | COMMENT | PENDING
        var doc = await SendJsonAsync(HttpMethod.Post, $"repos/{Esc(owner)}/{Esc(repo)}/pulls/{number}/reviews",
            Prune(new Dictionary<string, object?> { ["event"] = @event, ["body"] = body }), ct);
        return MapReview(doc!.Value);
    }

    public Task RequestReviewersAsync(string owner, string repo, long number, IReadOnlyList<string> reviewers, bool remove, CancellationToken ct = default)
        => SendJsonAsync(remove ? HttpMethod.Delete : HttpMethod.Post,
            $"repos/{Esc(owner)}/{Esc(repo)}/pulls/{number}/requested_reviewers", new { reviewers }, ct);

    private static ForgePullRequest MapPull(JsonElement p) => new(
        Number: Num(p, "number") ?? 0,
        Title: Str(p, "title") ?? "",
        Body: Str(p, "body"),
        State: Str(p, "state") ?? "",
        Merged: Bool(p, "merged"),
        Mergeable: Bool(p, "mergeable"),
        HeadRef: p.TryGetProperty("head", out var h) ? Str(h, "ref") : null,
        BaseRef: p.TryGetProperty("base", out var b) ? Str(b, "ref") : null,
        AuthorLogin: p.TryGetProperty("user", out var u) ? Str(u, "login") : null,
        HtmlUrl: Str(p, "html_url"));

    private static ForgeReview MapReview(JsonElement r) => new(
        Id: Num(r, "id") ?? 0,
        State: Str(r, "state"),
        Body: Str(r, "body"),
        ReviewerLogin: r.TryGetProperty("user", out var u) ? Str(u, "login") : null,
        SubmittedAt: r.TryGetProperty("submitted_at", out var dt) && dt.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(dt.GetString(), out var d) ? d : null,
        HtmlUrl: Str(r, "html_url"));
}
