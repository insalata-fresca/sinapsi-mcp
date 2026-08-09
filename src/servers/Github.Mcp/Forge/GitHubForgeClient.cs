using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sinapsi.Forge;
using Sinapsi.Forge.Model;

namespace Github.Mcp.Forge;

/// <summary>
/// <see cref="IForgeClient"/> over the GitHub REST API (raw HttpClient). GitHub mirrors the
/// Gitea API for most reads; the divergences are handled here:
///   • commit_files → the Git Data/Tree API (blobs → tree → commit → ref) for atomic, byte-safe
///     multi-file commits (GitHub has no Gitea ChangeFiles endpoint);
///   • search → the {items:[…]} wrapper;
///   • release asset upload → the uploads.github.com host;
///   • time-tracking → unsupported (Gitea-only) ⇒ <see cref="ForgeNotSupportedException"/>;
///   • insights (traffic / stats / community / SBOM) → GitHub-only, in
///     <c>GitHubForgeClient.Insights.cs</c>, which handles the 202 "still computing" and the
///     403 "traffic needs push access" answers as envelopes rather than exceptions.
/// Auth + Accept + User-Agent headers are set on the injected <see cref="HttpClient"/> by the host.
/// </summary>
public sealed partial class GitHubForgeClient(HttpClient http) : IForgeClient
{
    public ForgeProvider Provider => ForgeProvider.GitHub;
    public ForgeCapabilities Capabilities =>
        (ForgeCapabilities.All & ~ForgeCapabilities.TimeTracking) | ForgeCapabilities.Insights;

    // ── Users ────────────────────────────────────────────────────────────────
    public async Task<ForgeUser> GetMeAsync(CancellationToken ct = default)
        => MapUser(await GetJsonAsync("user", ct));

    public async Task<ForgeUser> GetUserAsync(string username, CancellationToken ct = default)
        => MapUser(await GetJsonAsync($"users/{Esc(username)}", ct));

    public async Task<IReadOnlyList<ForgeUser>> SearchUsersAsync(string query, int limit = 30, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync($"search/users?q={Esc(query)}&per_page={limit}", ct);
        var items = doc.TryGetProperty("items", out var it) ? it : doc;
        return items.EnumerateArray().Select(MapUser).ToList();
    }

    internal static ForgeUser MapUser(JsonElement u) => new(
        Login: Str(u, "login") ?? "",
        Id: Num(u, "id") ?? 0,
        FullName: Str(u, "name"),
        Email: Str(u, "email"),
        AvatarUrl: Str(u, "avatar_url"),
        HtmlUrl: Str(u, "html_url"),
        IsAdmin: Bool(u, "site_admin"));

    // ── HTTP plumbing (mirrors the Gitea adapter; GitHub paths) ────────────────
    internal async Task<JsonElement> GetJsonAsync(string path, CancellationToken ct)
    {
        using var resp = await http.GetAsync(path, ct);
        await EnsureOkAsync(resp, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.Clone();
    }

    internal async Task<JsonElement?> SendJsonAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null) req.Content = JsonContent.Create(body, options: J);
        using var resp = await http.SendAsync(req, ct);
        await EnsureOkAsync(resp, ct);
        if (resp.StatusCode == HttpStatusCode.NoContent) return null;
        var s = await resp.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrEmpty(s)) return null;
        using var doc = JsonDocument.Parse(s);
        return doc.RootElement.Clone();
    }

    private static async Task EnsureOkAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        // Status is the RAW verdict; the upstream body is scrubbed of any credential/key
        // material before it becomes the exception message a tool may surface.
        var body = await resp.Content.ReadAsStringAsync(ct);
        var safe = Sinapsi.Forge.Tools.SinapsiForgeErrors.Sanitize(body.Length > 600 ? body[..600] + "…" : body);
        throw new ForgeApiException((int)resp.StatusCode, $"{(int)resp.StatusCode} {resp.ReasonPhrase}: {safe}");
    }

    private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);
    internal static string Esc(string s) => Uri.EscapeDataString(s);
    internal static string EscPath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
    internal static Dictionary<string, object?> Prune(Dictionary<string, object?> d)
        => d.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value);

    internal static string? Str(JsonElement e, string n)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    internal static long? Num(JsonElement e, string n)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
    internal static bool? Bool(JsonElement e, string n)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : null;
}
