using System.Net.Http.Json;
using System.Text.Json;

namespace Zitadel.Mcp.Api;

/// <summary>
/// Minimal client over the ZITADEL REST API (raw <see cref="HttpClient"/>). The host sets
/// the base address + bearer token on the injected client; this type only shapes the
/// management/admin API paths and surfaces non-2xx responses as a
/// <see cref="ZitadelApiException"/> with the real status + body.
///
/// Only read endpoints are implemented — the server's tool surface is read-only.
/// </summary>
public sealed class ZitadelClient(HttpClient http)
{
    private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);

    // ── Users ────────────────────────────────────────────────────────────────

    /// <summary>List users (POST search; empty query returns the first page).</summary>
    public Task<JsonElement> ListUsersAsync(int limit, CancellationToken ct)
        => PostJsonAsync("management/v1/users/_search", new { query = new { limit } }, ct);

    /// <summary>Get a single user by id.</summary>
    public Task<JsonElement> GetUserAsync(string userId, CancellationToken ct)
        => GetJsonAsync($"management/v1/users/{Esc(userId)}", ct);

    // ── Projects ─────────────────────────────────────────────────────────────

    /// <summary>List projects (POST search; empty query returns the first page).</summary>
    public Task<JsonElement> ListProjectsAsync(int limit, CancellationToken ct)
        => PostJsonAsync("management/v1/projects/_search", new { query = new { limit } }, ct);

    // ── OIDC applications ────────────────────────────────────────────────────

    /// <summary>List the applications of a project.</summary>
    public Task<JsonElement> ListAppsAsync(string projectId, int limit, CancellationToken ct)
        => PostJsonAsync($"management/v1/projects/{Esc(projectId)}/apps/_search", new { query = new { limit } }, ct);

    /// <summary>Get a single application within a project.</summary>
    public Task<JsonElement> GetAppAsync(string projectId, string appId, CancellationToken ct)
        => GetJsonAsync($"management/v1/projects/{Esc(projectId)}/apps/{Esc(appId)}", ct);

    // ── HTTP plumbing (mirrors the forge adapters; ZITADEL paths) ──────────────

    private async Task<JsonElement> GetJsonAsync(string path, CancellationToken ct)
    {
        using var resp = await http.GetAsync(path, ct);
        await EnsureOkAsync(resp, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.Clone();
    }

    private async Task<JsonElement> PostJsonAsync(string path, object body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: J) };
        using var resp = await http.SendAsync(req, ct);
        await EnsureOkAsync(resp, ct);
        var s = await resp.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrEmpty(s)) return default;
        using var doc = JsonDocument.Parse(s);
        return doc.RootElement.Clone();
    }

    private static async Task EnsureOkAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync(ct);
        throw new ZitadelApiException((int)resp.StatusCode,
            $"{(int)resp.StatusCode} {resp.ReasonPhrase}: {(body.Length > 600 ? body[..600] + "…" : body)}");
    }

    private static string Esc(string s) => Uri.EscapeDataString(s);
}
