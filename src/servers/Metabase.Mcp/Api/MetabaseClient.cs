using System.Text.Json;

namespace Metabase.Mcp.Api;

/// <summary>
/// Minimal client over the Metabase REST API (raw <see cref="HttpClient"/>). The host sets
/// the base address + the <c>X-API-KEY</c> header on the injected client; this type only
/// shapes the <c>/api/</c> paths and surfaces non-2xx responses as a
/// <see cref="MetabaseApiException"/> with the real status + body.
///
/// Only read endpoints are implemented — the server's tool surface is read-only.
/// </summary>
public sealed class MetabaseClient(HttpClient http)
{
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

    // ── HTTP plumbing (mirrors the forge adapters; Metabase paths) ─────────────

    private async Task<JsonElement> GetJsonAsync(string path, CancellationToken ct)
    {
        using var resp = await http.GetAsync(path, ct);
        await EnsureOkAsync(resp, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
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
