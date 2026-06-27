using System.Text.Json;

namespace Sinapsi.Forge.Gitea;

public sealed partial class GiteaForgeClient
{
    // Gitea/Forgejo repo topics:
    //   GET    repos/{o}/{r}/topics          → {"topics": ["a","b"] | null}
    //   PUT    repos/{o}/{r}/topics/{topic}  → 204 (add one)
    //   DELETE repos/{o}/{r}/topics/{topic}  → 204 (remove one)
    //   PUT    repos/{o}/{r}/topics          → 204 ; body {"topics": [...]} (replace all)
    // The single-topic add/remove return no body, so we re-read to return the live set.

    public async Task<IReadOnlyList<string>> ListRepoTopicsAsync(string owner, string repo, CancellationToken ct = default)
        => ReadTopics(await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/topics", ct));

    public async Task<IReadOnlyList<string>> AddRepoTopicAsync(string owner, string repo, string topic, CancellationToken ct = default)
    {
        await SendJsonAsync(HttpMethod.Put, $"repos/{Esc(owner)}/{Esc(repo)}/topics/{Esc(topic)}", null, ct);
        return await ListRepoTopicsAsync(owner, repo, ct);
    }

    public async Task<IReadOnlyList<string>> RemoveRepoTopicAsync(string owner, string repo, string topic, CancellationToken ct = default)
    {
        await SendJsonAsync(HttpMethod.Delete, $"repos/{Esc(owner)}/{Esc(repo)}/topics/{Esc(topic)}", null, ct);
        return await ListRepoTopicsAsync(owner, repo, ct);
    }

    public async Task<IReadOnlyList<string>> SetRepoTopicsAsync(string owner, string repo, IReadOnlyList<string> topics, CancellationToken ct = default)
    {
        await SendJsonAsync(HttpMethod.Put, $"repos/{Esc(owner)}/{Esc(repo)}/topics",
            new Dictionary<string, object?> { ["topics"] = topics }, ct);
        return await ListRepoTopicsAsync(owner, repo, ct);
    }

    // {"topics": [...]}; "topics" may be null when the repo has none.
    private static IReadOnlyList<string> ReadTopics(JsonElement doc)
    {
        var arr = doc.ValueKind == JsonValueKind.Object && doc.TryGetProperty("topics", out var t) ? t : doc;
        return arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : [];
    }
}
