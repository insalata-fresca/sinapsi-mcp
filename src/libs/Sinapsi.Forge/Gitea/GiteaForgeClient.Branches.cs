using System.Text.Json;
using Sinapsi.Forge.Model;

namespace Sinapsi.Forge.Gitea;

public sealed partial class GiteaForgeClient
{
    public async Task<IReadOnlyList<ForgeBranch>> ListBranchesAsync(string owner, string repo, int limit = 50, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/branches?limit={limit}", ct);
        return doc.EnumerateArray().Select(MapBranch).ToList();
    }

    public async Task<ForgeBranch> GetBranchAsync(string owner, string repo, string branch, CancellationToken ct = default)
        => MapBranch(await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/branches/{EscPath(branch)}", ct));

    public async Task<ForgeBranch> CreateBranchAsync(string owner, string repo, string newBranch, string? fromBranch, CancellationToken ct = default)
    {
        var body = Prune(new Dictionary<string, object?>
        {
            ["new_branch_name"] = newBranch,
            ["old_branch_name"] = fromBranch,
        });
        var doc = await SendJsonAsync(HttpMethod.Post, $"repos/{Esc(owner)}/{Esc(repo)}/branches", body, ct);
        return MapBranch(doc!.Value);
    }

    public Task DeleteBranchAsync(string owner, string repo, string branch, CancellationToken ct = default)
        => SendJsonAsync(HttpMethod.Delete, $"repos/{Esc(owner)}/{Esc(repo)}/branches/{EscPath(branch)}", null, ct);

    private static ForgeBranch MapBranch(JsonElement b)
    {
        string sha = b.TryGetProperty("commit", out var c) ? (Str(c, "id") ?? Str(c, "sha") ?? "") : "";
        return new ForgeBranch(Str(b, "name") ?? "", sha, Bool(b, "protected"));
    }
}
