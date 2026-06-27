using System.Text.Json;
using Sinapsi.Forge.Model;

namespace Sinapsi.Forge.Gitea;

public sealed partial class GiteaForgeClient
{
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
        // /media returns the raw blob bytes (LFS-resolved); we base64 them for the caller.
        using var resp = await http.GetAsync($"repos/{Esc(owner)}/{Esc(repo)}/media/{EscPath(path)}{q}", ct);
        if (!resp.IsSuccessStatusCode)
            throw new ForgeApiException((int)resp.StatusCode, $"{(int)resp.StatusCode} fetching media {path}");
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        var mime = resp.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        return new ForgeBinary(path, null, bytes.LongLength, mime, Convert.ToBase64String(bytes));
    }

    public async Task<ForgeCommitResult> CreateOrUpdateFileAsync(string owner, string repo, string path, string contentBase64, string message, string branch, string? sha, CancellationToken ct = default)
    {
        var body = Prune(new Dictionary<string, object?>
        {
            ["content"] = contentBase64,
            ["message"] = message,
            ["branch"] = branch,
            ["sha"] = sha,
        });
        // POST creates; PUT updates (sha required). Gitea routes both on the same path.
        var method = string.IsNullOrWhiteSpace(sha) ? HttpMethod.Post : HttpMethod.Put;
        var doc = await SendJsonAsync(method, $"repos/{Esc(owner)}/{Esc(repo)}/contents/{EscPath(path)}", body, ct);
        return MapCommitResult(doc!.Value, branch, new[] { path });
    }

    public async Task<ForgeCommitResult> DeleteFileAsync(string owner, string repo, string path, string message, string branch, string sha, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?> { ["message"] = message, ["branch"] = branch, ["sha"] = sha };
        var doc = await SendJsonAsync(HttpMethod.Delete, $"repos/{Esc(owner)}/{Esc(repo)}/contents/{EscPath(path)}", body, ct);
        return MapCommitResult(doc!.Value, branch, new[] { path });
    }

    public async Task<ForgeCommitResult> CommitFilesAsync(string owner, string repo, string branch, string? newBranch, string message, IReadOnlyList<ForgeFileChange> files, CancellationToken ct = default)
    {
        var fileBodies = files.Select(f => Prune(new Dictionary<string, object?>
        {
            ["operation"] = f.Operation,
            ["path"] = f.Path,
            ["content"] = f.ContentBase64,   // EXACT bytes, base64 — byte-perfect for binary + patches
            ["sha"] = f.Sha,
            ["from_path"] = f.FromPath,
        })).ToList();

        var body = Prune(new Dictionary<string, object?>
        {
            ["branch"] = branch,
            ["new_branch"] = newBranch,
            ["message"] = message,
            ["files"] = fileBodies,
        });
        var doc = await SendJsonAsync(HttpMethod.Post, $"repos/{Esc(owner)}/{Esc(repo)}/contents", body, ct);
        return MapCommitResult(doc!.Value, newBranch ?? branch, files.Select(f => f.Path).ToList());
    }

    // ── mapping + helpers ────────────────────────────────────────────────────
    private static ForgeFile MapFile(JsonElement f) => new(
        Path: Str(f, "path") ?? "",
        Sha: Str(f, "sha"),
        Size: Num(f, "size"),
        Type: Str(f, "type") ?? "file",
        Encoding: Str(f, "encoding"),
        Content: Str(f, "content"),
        HtmlUrl: Str(f, "html_url"),
        DownloadUrl: Str(f, "download_url"));

    private static ForgeCommitResult MapCommitResult(JsonElement doc, string branch, IReadOnlyList<string> paths)
    {
        // ChangeFiles + single-file responses both nest a "commit" object.
        JsonElement commit = doc.TryGetProperty("commit", out var c) ? c : doc;
        return new ForgeCommitResult(
            CommitSha: Str(commit, "sha") ?? "",
            Branch: branch,
            HtmlUrl: Str(commit, "html_url") ?? Str(doc, "html_url"),
            Paths: paths);
    }

    /// <summary>Percent-escape each path segment but keep the slashes (Gitea wants a real path).</summary>
    internal static string EscPath(string path)
        => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
}
