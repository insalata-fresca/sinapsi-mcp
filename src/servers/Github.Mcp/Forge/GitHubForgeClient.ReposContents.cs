using System.Text.Json;
using Sinapsi.Forge;
using Sinapsi.Forge.Model;

namespace Github.Mcp.Forge;

public sealed partial class GitHubForgeClient
{
    // ── Repositories ───────────────────────────────────────────────────────────
    public async Task<ForgeRepo> GetRepoAsync(string owner, string repo, CancellationToken ct = default)
        => MapRepo(await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}", ct));

    public async Task<IReadOnlyList<ForgeRepo>> ListMyReposAsync(int limit = 50, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync($"user/repos?per_page={limit}", ct);
        return doc.EnumerateArray().Select(MapRepo).ToList();
    }

    public async Task<IReadOnlyList<ForgeRepo>> SearchReposAsync(string query, int limit = 30, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync($"search/repositories?q={Esc(query)}&per_page={limit}", ct);
        var items = doc.TryGetProperty("items", out var it) ? it : doc;
        return items.EnumerateArray().Select(MapRepo).ToList();
    }

    public async Task<ForgeRepo> CreateRepoAsync(CreateRepoRequest req, CancellationToken ct = default)
    {
        var body = Prune(new Dictionary<string, object?>
        {
            ["name"] = req.Name,
            ["description"] = req.Description,
            ["private"] = req.Private,
            ["auto_init"] = req.AutoInit,
            ["gitignore_template"] = req.Gitignores,
            ["license_template"] = req.License,
        });
        var path = string.IsNullOrWhiteSpace(req.Owner) ? "user/repos" : $"orgs/{Esc(req.Owner!)}/repos";
        return MapRepo((await SendJsonAsync(HttpMethod.Post, path, body, ct))!.Value);
    }

    public async Task<ForgeRepo> ForkRepoAsync(string owner, string repo, string? organization, CancellationToken ct = default)
    {
        object? body = string.IsNullOrWhiteSpace(organization) ? null : new { organization };
        return MapRepo((await SendJsonAsync(HttpMethod.Post, $"repos/{Esc(owner)}/{Esc(repo)}/forks", body, ct))!.Value);
    }

    public async Task<ForgeRepo> EditRepoAsync(string owner, string repo, EditRepoRequest req, CancellationToken ct = default)
    {
        var body = Prune(new Dictionary<string, object?>
        {
            ["description"] = req.Description,
            ["private"] = req.Private,
            ["default_branch"] = req.DefaultBranch,
            ["has_issues"] = req.HasIssues,
            ["has_wiki"] = req.HasWiki,
            ["archived"] = req.Archived,
            // GitHub's name for the same setting is `delete_branch_on_merge`, NOT the
            // Forgejo/Gitea `default_delete_branch_after_merge`. Pruned when null.
            ["delete_branch_on_merge"] = req.DefaultDeleteBranchAfterMerge,
            // GitHub calls the homepage `homepage` (Forgejo: `website`). Pruned when null;
            // an explicit "" clears it. Without this the About sidebar could not be set at all.
            ["homepage"] = req.Homepage,
        });
        return MapRepo((await SendJsonAsync(HttpMethod.Patch, $"repos/{Esc(owner)}/{Esc(repo)}", body, ct))!.Value);
    }

    public Task DeleteRepoAsync(string owner, string repo, CancellationToken ct = default)
        => SendJsonAsync(HttpMethod.Delete, $"repos/{Esc(owner)}/{Esc(repo)}", null, ct);

    internal static ForgeRepo MapRepo(JsonElement r) => new(
        Owner: r.TryGetProperty("owner", out var o) ? (Str(o, "login") ?? "") : "",
        Name: Str(r, "name") ?? "",
        FullName: Str(r, "full_name") ?? "",
        Private: Bool(r, "private") ?? false,
        Fork: Bool(r, "fork") ?? false,
        Description: Str(r, "description"),
        DefaultBranch: Str(r, "default_branch") ?? "",
        CloneUrl: Str(r, "clone_url"),
        SshUrl: Str(r, "ssh_url"),
        HtmlUrl: Str(r, "html_url"),
        Stars: Num(r, "stargazers_count") ?? Num(r, "stars_count"),
        Forks: Num(r, "forks_count"),
        OpenIssues: Num(r, "open_issues_count"),
        Homepage: Str(r, "homepage"));

    // ── Contents / files ───────────────────────────────────────────────────────
    public async Task<ForgeContentListing> GetContentsAsync(string owner, string repo, string path, string? gitRef, CancellationToken ct = default)
    {
        var q = string.IsNullOrWhiteSpace(gitRef) ? "" : $"?ref={Esc(gitRef!)}";
        var doc = await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/contents/{EscPath(path)}{q}", ct);
        if (doc.ValueKind == JsonValueKind.Array)
        {
            var entries = doc.EnumerateArray().Select(e => new ForgeDirEntry(
                Str(e, "name") ?? "", Str(e, "path") ?? "", Str(e, "type") ?? "", Num(e, "size"), Str(e, "sha"))).ToList();
            return new ForgeContentListing(path, "dir", null, entries);
        }
        return new ForgeContentListing(path, "file", MapFile(doc), null);
    }

    public async Task<ForgeBinary> GetFileBinaryAsync(string owner, string repo, string path, string? gitRef, CancellationToken ct = default)
    {
        var q = string.IsNullOrWhiteSpace(gitRef) ? "" : $"?ref={Esc(gitRef!)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, $"repos/{Esc(owner)}/{Esc(repo)}/contents/{EscPath(path)}{q}");
        req.Headers.Accept.ParseAdd("application/vnd.github.raw+json");
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new ForgeApiException((int)resp.StatusCode, $"{(int)resp.StatusCode} fetching raw {path}");
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        var mime = resp.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        return new ForgeBinary(path, null, bytes.LongLength, mime, Convert.ToBase64String(bytes));
    }

    public async Task<ForgeCommitResult> CreateOrUpdateFileAsync(string owner, string repo, string path, string contentBase64, string message, string branch, string? sha, CancellationToken ct = default)
    {
        var body = Prune(new Dictionary<string, object?>
        {
            ["message"] = message, ["content"] = contentBase64, ["branch"] = branch, ["sha"] = sha,
        });
        // GitHub uses PUT for both create and update (sha distinguishes).
        var doc = await SendJsonAsync(HttpMethod.Put, $"repos/{Esc(owner)}/{Esc(repo)}/contents/{EscPath(path)}", body, ct);
        return MapContentsCommit(doc!.Value, branch, new[] { path });
    }

    public async Task<ForgeCommitResult> DeleteFileAsync(string owner, string repo, string path, string message, string branch, string sha, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?> { ["message"] = message, ["branch"] = branch, ["sha"] = sha };
        var doc = await SendJsonAsync(HttpMethod.Delete, $"repos/{Esc(owner)}/{Esc(repo)}/contents/{EscPath(path)}", body, ct);
        return MapContentsCommit(doc!.Value, branch, new[] { path });
    }

    /// <summary>
    /// GitHub has no atomic ChangeFiles endpoint — build one commit via the Git Data API:
    /// blobs (base64) → tree (on the base tree) → commit (parent = branch head) → move the ref.
    /// </summary>
    public async Task<ForgeCommitResult> CommitFilesAsync(string owner, string repo, string branch, string? newBranch, string message, IReadOnlyList<ForgeFileChange> files, CancellationToken ct = default)
    {
        var basePath = $"repos/{Esc(owner)}/{Esc(repo)}";
        var refDoc = await GetJsonAsync($"{basePath}/git/ref/heads/{EscPath(branch)}", ct);
        var baseCommitSha = refDoc.GetProperty("object").GetProperty("sha").GetString()!;
        var commitDoc = await GetJsonAsync($"{basePath}/git/commits/{baseCommitSha}", ct);
        var baseTreeSha = commitDoc.GetProperty("tree").GetProperty("sha").GetString()!;

        var treeItems = new List<Dictionary<string, object?>>();
        foreach (var f in files)
        {
            if (string.Equals(f.Operation, "delete", StringComparison.OrdinalIgnoreCase))
            {
                // sha:null removes the path from the tree — MUST be sent (not pruned).
                treeItems.Add(new() { ["path"] = f.Path, ["mode"] = "100644", ["type"] = "blob", ["sha"] = null });
                continue;
            }
            var blob = await SendJsonAsync(HttpMethod.Post, $"{basePath}/git/blobs",
                new { content = f.ContentBase64 ?? "", encoding = "base64" }, ct);
            var blobSha = blob!.Value.GetProperty("sha").GetString();
            treeItems.Add(new() { ["path"] = f.Path, ["mode"] = "100644", ["type"] = "blob", ["sha"] = blobSha });
        }

        var treeResp = await SendJsonAsync(HttpMethod.Post, $"{basePath}/git/trees",
            new { base_tree = baseTreeSha, tree = treeItems }, ct);
        var newTreeSha = treeResp!.Value.GetProperty("sha").GetString();

        var commitResp = await SendJsonAsync(HttpMethod.Post, $"{basePath}/git/commits",
            new { message, tree = newTreeSha, parents = new[] { baseCommitSha } }, ct);
        var newCommitSha = commitResp!.Value.GetProperty("sha").GetString()!;

        var target = newBranch ?? branch;
        if (!string.IsNullOrWhiteSpace(newBranch))
            await SendJsonAsync(HttpMethod.Post, $"{basePath}/git/refs", new { @ref = $"refs/heads/{newBranch}", sha = newCommitSha }, ct);
        else
            await SendJsonAsync(HttpMethod.Patch, $"{basePath}/git/refs/heads/{EscPath(branch)}", new { sha = newCommitSha, force = false }, ct);

        return new ForgeCommitResult(newCommitSha, target, Str(commitResp.Value, "html_url"), files.Select(f => f.Path).ToList());
    }

    private static ForgeFile MapFile(JsonElement f) => new(
        Path: Str(f, "path") ?? "", Sha: Str(f, "sha"), Size: Num(f, "size"),
        Type: Str(f, "type") ?? "file", Encoding: Str(f, "encoding"), Content: Str(f, "content"),
        HtmlUrl: Str(f, "html_url"), DownloadUrl: Str(f, "download_url"));

    private static ForgeCommitResult MapContentsCommit(JsonElement doc, string branch, IReadOnlyList<string> paths)
    {
        JsonElement commit = doc.TryGetProperty("commit", out var c) ? c : doc;
        return new ForgeCommitResult(Str(commit, "sha") ?? "", branch, Str(commit, "html_url"), paths);
    }

    // ── Branches ─────────────────────────────────────────────────────────────
    public async Task<IReadOnlyList<ForgeBranch>> ListBranchesAsync(string owner, string repo, int limit = 50, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/branches?per_page={limit}", ct);
        return doc.EnumerateArray().Select(MapBranch).ToList();
    }

    public async Task<ForgeBranch> GetBranchAsync(string owner, string repo, string branch, CancellationToken ct = default)
        => MapBranch(await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/branches/{EscPath(branch)}", ct));

    public async Task<ForgeBranch> CreateBranchAsync(string owner, string repo, string newBranch, string? fromBranch, CancellationToken ct = default)
    {
        var src = fromBranch;
        if (string.IsNullOrWhiteSpace(src))
            src = (await GetRepoAsync(owner, repo, ct)).DefaultBranch;
        var refDoc = await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/git/ref/heads/{EscPath(src!)}", ct);
        var sha = refDoc.GetProperty("object").GetProperty("sha").GetString();
        await SendJsonAsync(HttpMethod.Post, $"repos/{Esc(owner)}/{Esc(repo)}/git/refs", new { @ref = $"refs/heads/{newBranch}", sha }, ct);
        return new ForgeBranch(newBranch, sha ?? "", null);
    }

    public Task DeleteBranchAsync(string owner, string repo, string branch, CancellationToken ct = default)
        => SendJsonAsync(HttpMethod.Delete, $"repos/{Esc(owner)}/{Esc(repo)}/git/refs/heads/{EscPath(branch)}", null, ct);

    private static ForgeBranch MapBranch(JsonElement b)
    {
        string sha = b.TryGetProperty("commit", out var c) ? (Str(c, "sha") ?? "") : "";
        return new ForgeBranch(Str(b, "name") ?? "", sha, Bool(b, "protected"));
    }

    // ── Commits ──────────────────────────────────────────────────────────────
    public async Task<IReadOnlyList<ForgeCommit>> ListCommitsAsync(string owner, string repo, string? sha, int limit = 30, CancellationToken ct = default)
    {
        var q = string.IsNullOrWhiteSpace(sha) ? $"?per_page={limit}" : $"?sha={Esc(sha!)}&per_page={limit}";
        var doc = await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/commits{q}", ct);
        return doc.EnumerateArray().Select(MapCommit).ToList();
    }

    public async Task<ForgeCommit> GetCommitAsync(string owner, string repo, string sha, CancellationToken ct = default)
        => MapCommit(await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/commits/{Esc(sha)}", ct));

    private static ForgeCommit MapCommit(JsonElement c)
    {
        string message = "";
        ForgeCommitAuthor? author = null;
        if (c.TryGetProperty("commit", out var inner))
        {
            message = Str(inner, "message") ?? "";
            if (inner.TryGetProperty("author", out var a))
                author = new ForgeCommitAuthor(Str(a, "name"), Str(a, "email"),
                    a.TryGetProperty("date", out var dt) && dt.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(dt.GetString(), out var d) ? d : null);
        }
        return new ForgeCommit(Str(c, "sha") ?? "", message, author, Str(c, "html_url"));
    }
}
