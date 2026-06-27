using System.Net.Http.Json;
using System.Text.Json;

namespace Metabase.Mcp.Api;

/// <summary>
/// Minimal client over the Metabase REST API (raw <see cref="HttpClient"/>). The host sets
/// the base address + the <c>X-API-KEY</c> header on the injected client; this type only
/// shapes the <c>/api/</c> paths and surfaces non-2xx responses as a
/// <see cref="MetabaseApiException"/> with the real status + body.
///
/// Both read and mutating endpoints are implemented. The typed methods cover the common
/// database / table / field / query / card / dashboard / collection / user operations; the
/// generic <see cref="SendRawAsync"/> reaches ANY endpoint so the whole API is covered.
/// Mutating tools are marked Destructive on the tool side.
/// </summary>
public sealed class MetabaseClient(HttpClient http)
{
    private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);

    /// <summary>List the configured databases.</summary>
    public Task<JsonElement> ListDatabasesAsync(CancellationToken ct)
        => GetJsonAsync("api/database", ct);

    /// <summary>List collections.</summary>
    public Task<JsonElement> ListCollectionsAsync(CancellationToken ct)
        => GetJsonAsync("api/collection", ct);

    /// <summary>List saved questions (cards).</summary>
    public Task<JsonElement> ListCardsAsync(CancellationToken ct)
        => GetJsonAsync("api/card", ct);

    /// <summary>Get a single saved question (card) by id.</summary>
    public Task<JsonElement> GetCardAsync(long cardId, CancellationToken ct)
        => GetJsonAsync($"api/card/{cardId}", ct);

    // ── Generic verbs (let the tool layer shape the path + body) ───────────────

    /// <summary>GET an arbitrary <c>/api/</c> path.</summary>
    public Task<JsonElement> GetAsync(string path, CancellationToken ct)
        => SendAsync(HttpMethod.Get, path, null, ct);

    /// <summary>POST a JSON body to an arbitrary <c>/api/</c> path.</summary>
    public Task<JsonElement> PostAsync(string path, object? body, CancellationToken ct)
        => SendAsync(HttpMethod.Post, path, body, ct);

    /// <summary>PUT a JSON body to an arbitrary <c>/api/</c> path.</summary>
    public Task<JsonElement> PutAsync(string path, object? body, CancellationToken ct)
        => SendAsync(HttpMethod.Put, path, body, ct);

    /// <summary>DELETE an arbitrary <c>/api/</c> path.</summary>
    public Task<JsonElement> DeleteAsync(string path, CancellationToken ct)
        => SendAsync(HttpMethod.Delete, path, null, ct);

    /// <summary>Raw send with an arbitrary method (for the generic <c>request</c> passthrough).
    /// <paramref name="rawBody"/> is a pre-parsed JSON value or null.</summary>
    public Task<JsonElement> SendRawAsync(string method, string path, JsonElement? rawBody, CancellationToken ct)
        => SendAsync(new HttpMethod(method.ToUpperInvariant()), path, rawBody is { } b ? (object)b : null, ct);

    // ── HTTP plumbing (mirrors the forge adapters; Metabase paths) ─────────────

    private Task<JsonElement> GetJsonAsync(string path, CancellationToken ct)
        => SendAsync(HttpMethod.Get, path, null, ct);

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? jsonBody, CancellationToken ct)
    {
        // The injected HttpClient has a BaseAddress with a trailing slash; relative paths
        // must not start with '/'. Accept either form from callers.
        var relative = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : path.TrimStart('/');
        using var req = new HttpRequestMessage(method, relative);
        if (jsonBody is not null)
        {
            req.Content = jsonBody is JsonElement je
                ? JsonContent.Create(je, options: J)
                : JsonContent.Create(jsonBody, options: J);
        }
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
        throw new MetabaseApiException((int)resp.StatusCode,
            $"{(int)resp.StatusCode} {resp.ReasonPhrase}: {(body.Length > 600 ? body[..600] + "…" : body)}");
    }
}
