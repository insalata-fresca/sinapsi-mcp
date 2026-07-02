using System.Text.Json;
using Sinapsi.Forge.Model;

namespace Sinapsi.Forge.Gitea;

public sealed partial class GiteaForgeClient
{
    public async Task<ForgeRepo> GetRepoAsync(string owner, string repo, CancellationToken ct = default)
        => MapRepo(await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}", ct));

    public async Task<IReadOnlyList<ForgeRepo>> ListMyReposAsync(int limit = 50, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync($"user/repos?limit={limit}", ct);
        return doc.EnumerateArray().Select(MapRepo).ToList();
    }

    public async Task<IReadOnlyList<ForgeRepo>> SearchReposAsync(string query, int limit = 30, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync($"repos/search?q={Esc(query)}&limit={limit}", ct);
        var data = doc.TryGetProperty("data", out var d) ? d : doc;
        return data.EnumerateArray().Select(MapRepo).ToList();
    }

    public async Task<ForgeRepo> CreateRepoAsync(CreateRepoRequest req, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = req.Name,
            ["description"] = req.Description,
            ["private"] = req.Private,
            ["auto_init"] = req.AutoInit,
            ["default_branch"] = req.DefaultBranch,
            ["license"] = req.License,
            ["gitignores"] = req.Gitignores,
            ["readme"] = req.Readme,
        };
        // null owner => the authenticated user (/user/repos); else an organization.
        var path = string.IsNullOrWhiteSpace(req.Owner) ? "user/repos" : $"orgs/{Esc(req.Owner!)}/repos";
        var doc = await SendJsonAsync(HttpMethod.Post, path, Prune(body), ct);
        return MapRepo(doc!.Value);
    }

    public async Task<ForgeRepo> ForkRepoAsync(string owner, string repo, string? organization, CancellationToken ct = default)
    {
        object? body = string.IsNullOrWhiteSpace(organization) ? null : new { organization };
        var doc = await SendJsonAsync(HttpMethod.Post, $"repos/{Esc(owner)}/{Esc(repo)}/forks", body, ct);
        return MapRepo(doc!.Value);
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
            ["has_releases"] = req.HasReleases,
            ["archived"] = req.Archived,
        });
        var doc = await SendJsonAsync(HttpMethod.Patch, $"repos/{Esc(owner)}/{Esc(repo)}", body, ct);
        return MapRepo(doc!.Value);
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
        Stars: Num(r, "stars_count"),
        Forks: Num(r, "forks_count"),
        OpenIssues: Num(r, "open_issues_count"));

    /// <summary>Drop null values so the forge applies its own defaults.</summary>
    internal static Dictionary<string, object?> Prune(Dictionary<string, object?> d)
        => d.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value);
}
