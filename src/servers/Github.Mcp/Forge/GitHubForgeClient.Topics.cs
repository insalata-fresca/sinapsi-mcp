using System.Text.Json;

namespace Github.Mcp.Forge;

public sealed partial class GitHubForgeClient
{
    // GitHub repo topics:
    //   GET repos/{o}/{r}/topics             → {"names": [...]}
    //   PUT repos/{o}/{r}/topics ; body {"names":[...]} → 200 {"names":[...]} (replace all)
    // GitHub has no per-topic add/delete endpoint, so add/remove are read-modify-write
    // over the replace call. (Tools are wired only on the forgejo host; this keeps the
    // IForgeClient contract honest for github-mcp rather than throwing NotSupported.)

    public async Task<IReadOnlyList<string>> ListRepoTopicsAsync(string owner, string repo, CancellationToken ct = default)
        => ReadNames(await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/topics", ct));

    public async Task<IReadOnlyList<string>> AddRepoTopicAsync(string owner, string repo, string topic, CancellationToken ct = default)
    {
        var names = new List<string>(await ListRepoTopicsAsync(owner, repo, ct));
        if (!names.Contains(topic, StringComparer.OrdinalIgnoreCase)) names.Add(topic);
        return await SetRepoTopicsAsync(owner, repo, names, ct);
    }

    public async Task<IReadOnlyList<string>> RemoveRepoTopicAsync(string owner, string repo, string topic, CancellationToken ct = default)
    {
        var names = (await ListRepoTopicsAsync(owner, repo, ct))
            .Where(t => !string.Equals(t, topic, StringComparison.OrdinalIgnoreCase)).ToList();
        return await SetRepoTopicsAsync(owner, repo, names, ct);
    }

    public async Task<IReadOnlyList<string>> SetRepoTopicsAsync(string owner, string repo, IReadOnlyList<string> topics, CancellationToken ct = default)
    {
        var doc = await SendJsonAsync(HttpMethod.Put, $"repos/{Esc(owner)}/{Esc(repo)}/topics",
            new Dictionary<string, object?> { ["names"] = topics }, ct);
        return doc is { } d ? ReadNames(d) : topics;
    }

    // {"names": [...]}
    private static IReadOnlyList<string> ReadNames(JsonElement doc)
    {
        var arr = doc.ValueKind == JsonValueKind.Object && doc.TryGetProperty("names", out var n) ? n : doc;
        return arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : [];
    }
}
