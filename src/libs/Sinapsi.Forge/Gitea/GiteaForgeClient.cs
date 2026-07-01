using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sinapsi.Forge.Model;

namespace Sinapsi.Forge.Gitea;

/// <summary>
/// <see cref="IForgeClient"/> over the Gitea/Forgejo REST API (v1). Drives both
/// Forgejo (self-hosted) and Codeberg (a Forgejo instance) — only the base URL +
/// token differ, supplied via the injected <see cref="HttpClient"/> (BaseAddress
/// = <c>https://&lt;host&gt;/api/v1/</c>, <c>Authorization: token &lt;PAT&gt;</c>).
/// </summary>
public sealed partial class GiteaForgeClient(HttpClient http) : IForgeClient
{
    private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);

    public ForgeProvider Provider => ForgeProvider.Gitea;

    public ForgeCapabilities Capabilities =>
        ForgeCapabilities.All | ForgeCapabilities.TimeTracking;

    // ── Users ────────────────────────────────────────────────────────────────
    public async Task<ForgeUser> GetMeAsync(CancellationToken ct = default)
        => MapUser(await GetJsonAsync("user", ct));

    public async Task<ForgeUser> GetUserAsync(string username, CancellationToken ct = default)
        => MapUser(await GetJsonAsync($"users/{Esc(username)}", ct));

    public async Task<IReadOnlyList<ForgeUser>> SearchUsersAsync(string query, int limit = 30, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync($"users/search?q={Esc(query)}&limit={limit}", ct);
        var data = doc.TryGetProperty("data", out var d) ? d : doc;
        return data.EnumerateArray().Select(MapUser).ToList();
    }

    // ── mapping ────────────────────────────────────────────────────────────────
    private static ForgeUser MapUser(JsonElement u) => new(
        Login: Str(u, "login") ?? "",
        Id: Num(u, "id") ?? 0,
        FullName: Str(u, "full_name"),
        Email: Str(u, "email"),
        AvatarUrl: Str(u, "avatar_url"),
        HtmlUrl: Str(u, "html_url"),
        IsAdmin: Bool(u, "is_admin"));

    // ── HTTP plumbing ──────────────────────────────────────────────────────────
    private async Task<JsonElement> GetJsonAsync(string path, CancellationToken ct)
    {
        using var resp = await http.GetAsync(path, ct);
        await EnsureOkAsync(resp, ct);
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.Clone();
    }

    internal async Task<JsonElement?> SendJsonAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null) req.Content = JsonContent.Create(body, options: J);
        using var resp = await http.SendAsync(req, ct);
        await EnsureOkAsync(resp, ct);
        if (resp.StatusCode == HttpStatusCode.NoContent) return null;
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        if (stream.Length == 0) return null;
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.Clone();
    }

    private static async Task EnsureOkAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        // Status is the RAW verdict; the upstream body is scrubbed of any credential/key
        // material before it becomes the exception message a tool may surface.
        var body = await resp.Content.ReadAsStringAsync(ct);
        var safe = Tools.SinapsiForgeErrors.Sanitize(Trim(body));
        throw new ForgeApiException((int)resp.StatusCode, $"{(int)resp.StatusCode} {resp.ReasonPhrase}: {safe}");
    }

    private static string Trim(string s) => s.Length > 600 ? s[..600] + "…" : s;
    internal static string Esc(string s) => Uri.EscapeDataString(s);

    internal static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    internal static long? Num(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
    internal static bool? Bool(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : null;
}
