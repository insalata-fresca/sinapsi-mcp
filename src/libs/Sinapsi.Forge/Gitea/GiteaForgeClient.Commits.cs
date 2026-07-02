using System.Text.Json;
using Sinapsi.Forge.Model;

namespace Sinapsi.Forge.Gitea;

public sealed partial class GiteaForgeClient
{
    public async Task<IReadOnlyList<ForgeCommit>> ListCommitsAsync(string owner, string repo, string? sha, int limit = 30, CancellationToken ct = default)
    {
        var q = string.IsNullOrWhiteSpace(sha) ? $"?limit={limit}" : $"?sha={Esc(sha!)}&limit={limit}";
        var doc = await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/commits{q}", ct);
        return doc.EnumerateArray().Select(MapCommit).ToList();
    }

    public async Task<ForgeCommit> GetCommitAsync(string owner, string repo, string sha, CancellationToken ct = default)
        => MapCommit(await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/git/commits/{Esc(sha)}", ct));

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
