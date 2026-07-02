using System.Text.Json;
using Sinapsi.Forge.Model;

namespace Sinapsi.Forge.Gitea;

public sealed partial class GiteaForgeClient
{
    public async Task<IReadOnlyList<ForgeRelease>> ListReleasesAsync(string owner, string repo, int limit = 30, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/releases?limit={limit}", ct);
        return doc.EnumerateArray().Select(MapRelease).ToList();
    }

    public async Task<ForgeRelease> GetLatestReleaseAsync(string owner, string repo, CancellationToken ct = default)
        => MapRelease(await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/releases/latest", ct));

    public async Task<ForgeRelease> CreateReleaseAsync(string owner, string repo, CreateReleaseRequest req, CancellationToken ct = default)
    {
        var body = Prune(new Dictionary<string, object?>
        {
            ["tag_name"] = req.TagName,
            ["name"] = req.Name,
            ["body"] = req.Body,
            ["target_commitish"] = req.TargetCommitish,
            ["draft"] = req.Draft,
            ["prerelease"] = req.Prerelease,
        });
        var doc = await SendJsonAsync(HttpMethod.Post, $"repos/{Esc(owner)}/{Esc(repo)}/releases", body, ct);
        return MapRelease(doc!.Value);
    }

    public async Task<ForgeReleaseAsset> UploadReleaseAssetAsync(string owner, string repo, long releaseId, string name, string? contentBase64, string? sourcePath = null, string? sourceUrl = null, CancellationToken ct = default)
    {
        // Resolve the asset bytes from exactly one of file path / URL / inline base64.
        // Path + URL stream (not buffered) so a 20MB+ binary doesn't sit fully in memory.
        var (assetContent, dispose) = await ReleaseAssetContent.ResolveAsync(contentBase64, sourcePath, sourceUrl, ct);
        try
        {
            using var form = new MultipartFormDataContent { { assetContent, "attachment", name } };
            using var resp = await http.PostAsync($"repos/{Esc(owner)}/{Esc(repo)}/releases/{releaseId}/assets?name={Esc(name)}", form, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(ct);
                throw new ForgeApiException((int)resp.StatusCode, $"{(int)resp.StatusCode} {resp.ReasonPhrase} uploading asset {name}: {Trim(errBody)}");
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return MapAsset(doc.RootElement);
        }
        finally { dispose?.Dispose(); }
    }

    public async Task<ForgeRelease> GetReleaseAsync(string owner, string repo, long releaseId, CancellationToken ct = default)
        => MapRelease(await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/releases/{releaseId}", ct));

    public async Task<ForgeRelease> GetReleaseByTagAsync(string owner, string repo, string tag, CancellationToken ct = default)
        => MapRelease(await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/releases/tags/{Esc(tag)}", ct));

    public async Task<ForgeRelease> EditReleaseAsync(string owner, string repo, long releaseId, EditReleaseRequest req, CancellationToken ct = default)
    {
        var body = Prune(new Dictionary<string, object?>
        {
            ["tag_name"] = req.TagName,
            ["target_commitish"] = req.TargetCommitish,
            ["name"] = req.Name,
            ["body"] = req.Body,
            ["draft"] = req.Draft,
            ["prerelease"] = req.Prerelease,
        });
        var doc = await SendJsonAsync(HttpMethod.Patch, $"repos/{Esc(owner)}/{Esc(repo)}/releases/{releaseId}", body, ct);
        return MapRelease(doc!.Value);
    }

    public Task DeleteReleaseAsync(string owner, string repo, long releaseId, CancellationToken ct = default)
        => SendJsonAsync(HttpMethod.Delete, $"repos/{Esc(owner)}/{Esc(repo)}/releases/{releaseId}", null, ct);

    public async Task<IReadOnlyList<ForgeReleaseAsset>> ListReleaseAssetsAsync(string owner, string repo, long releaseId, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/releases/{releaseId}/assets", ct);
        return doc.EnumerateArray().Select(MapAsset).ToList();
    }

    public async Task<ForgeReleaseAsset> EditReleaseAssetAsync(string owner, string repo, long releaseId, long assetId, string name, CancellationToken ct = default)
    {
        var doc = await SendJsonAsync(HttpMethod.Patch, $"repos/{Esc(owner)}/{Esc(repo)}/releases/{releaseId}/assets/{assetId}", new { name }, ct);
        return MapAsset(doc!.Value);
    }

    public Task DeleteReleaseAssetAsync(string owner, string repo, long releaseId, long assetId, CancellationToken ct = default)
        => SendJsonAsync(HttpMethod.Delete, $"repos/{Esc(owner)}/{Esc(repo)}/releases/{releaseId}/assets/{assetId}", null, ct);

    public async Task<IReadOnlyList<ForgeTag>> ListTagsAsync(string owner, string repo, int limit = 30, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/tags?limit={limit}", ct);
        return doc.EnumerateArray().Select(t => new ForgeTag(
            Str(t, "name") ?? "",
            t.TryGetProperty("commit", out var c) ? Str(c, "sha") : null)).ToList();
    }

    private static ForgeRelease MapRelease(JsonElement r) => new(
        Id: Num(r, "id") ?? 0,
        TagName: Str(r, "tag_name") ?? "",
        Name: Str(r, "name"),
        Body: Str(r, "body"),
        Draft: Bool(r, "draft") ?? false,
        Prerelease: Bool(r, "prerelease") ?? false,
        HtmlUrl: Str(r, "html_url"),
        Assets: r.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(MapAsset).ToList() : []);

    private static ForgeReleaseAsset MapAsset(JsonElement a) => new(
        Id: Num(a, "id") ?? 0, Name: Str(a, "name") ?? "", Size: Num(a, "size"),
        DownloadUrl: Str(a, "browser_download_url") ?? Str(a, "download_url"));
}
