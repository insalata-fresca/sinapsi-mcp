using System.Net.Http.Json;
using System.Text.Json;
using Zitadel.Mcp;

namespace Zitadel.Mcp.Api;

/// <summary>
/// Minimal client over the ZITADEL REST API (raw <see cref="HttpClient"/>). The host sets
/// the base address + bearer token on the injected client; this type only shapes the
/// management/admin API paths and surfaces non-2xx responses as a
/// <see cref="ZitadelApiException"/> with the real status + body.
///
/// Both read and mutating endpoints are implemented. The read surface (users, projects,
/// OIDC apps) is free; the mutating surface (project/app CRUD, OIDC-secret rotation, and
/// the machine-identity lifecycle: create/update/delete machine user, PAT + JSON key
/// issuance) backs machine-to-machine (M2M) credential provisioning and is marked
/// Destructive on its tools.
/// </summary>
public sealed class ZitadelClient(HttpClient http, ZitadelConfig opts)
{
    private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);

    /// <summary>Host-side directory <c>create_machine_key</c> writes a machine user's JSON
    /// private key into (from <c>AGENT_KEY_DIR</c>). Exposed as a read-through on the client so
    /// the tool needs ONLY the <see cref="ZitadelClient"/> DI param — a second injected
    /// parameter interleaved before the tool params breaks the MCP SDK's reflection marshaller
    /// (it fails to bind the first tool param, e.g. <c>userId</c>). See ZitadelClient's single
    /// leading-DI-param contract shared by every sibling tool.</summary>
    public string AgentKeyDir => opts.AgentKeyDir;

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

    /// <summary>Create a new project. Returns {id, details}. MUTATES.</summary>
    public Task<JsonElement> CreateProjectAsync(string name, CancellationToken ct)
        => PostJsonAsync("management/v1/projects", new { name }, ct);

    // ── OIDC applications ────────────────────────────────────────────────────

    /// <summary>List the applications of a project.</summary>
    public Task<JsonElement> ListAppsAsync(string projectId, int limit, CancellationToken ct)
        => PostJsonAsync($"management/v1/projects/{Esc(projectId)}/apps/_search", new { query = new { limit } }, ct);

    /// <summary>Get a single application within a project.</summary>
    public Task<JsonElement> GetAppAsync(string projectId, string appId, CancellationToken ct)
        => GetJsonAsync($"management/v1/projects/{Esc(projectId)}/apps/{Esc(appId)}", ct);

    /// <summary>Create an OIDC application (= client). MUTATES.</summary>
    public Task<JsonElement> CreateOidcAppAsync(string projectId, object body, CancellationToken ct)
        => PostJsonAsync($"management/v1/projects/{Esc(projectId)}/apps/oidc", body, ct);

    /// <summary>Update an OIDC app's config (only provided fields sent). MUTATES.</summary>
    public Task<JsonElement> UpdateOidcAppConfigAsync(string projectId, string appId, object body, CancellationToken ct)
        => PutJsonAsync($"management/v1/projects/{Esc(projectId)}/apps/{Esc(appId)}/oidc_config", body, ct);

    /// <summary>Delete an application by id. MUTATES.</summary>
    public Task<JsonElement> DeleteAppAsync(string projectId, string appId, CancellationToken ct)
        => DeleteJsonAsync($"management/v1/projects/{Esc(projectId)}/apps/{Esc(appId)}", ct);

    /// <summary>Rotate the OIDC client secret. Response carries the new secret. MUTATES + SENSITIVE.</summary>
    public Task<JsonElement> RegenerateOidcSecretAsync(string projectId, string appId, CancellationToken ct)
        => PostJsonAsync($"management/v1/projects/{Esc(projectId)}/apps/{Esc(appId)}/oidc_config/_change_client_secret", new { }, ct);

    // ── Machine (service) users — the M2M identity-minting surface ─────────────

    /// <summary>Create a machine (service) user. Returns {userId, details}. MUTATES.</summary>
    public Task<JsonElement> CreateMachineUserAsync(object body, CancellationToken ct)
        => PostJsonAsync("management/v1/users/machine", body, ct);

    /// <summary>Update a machine (service) user — name/description/access-token type. MUTATES.</summary>
    public Task<JsonElement> UpdateMachineUserAsync(string userId, object body, CancellationToken ct)
        => PutJsonAsync($"management/v1/users/{Esc(userId)}/machine", body, ct);

    /// <summary>Delete a user by id (RemoveUser). IRREVERSIBLE. MUTATES.</summary>
    public Task<JsonElement> DeleteMachineUserAsync(string userId, CancellationToken ct)
        => DeleteJsonAsync($"management/v1/users/{Esc(userId)}", ct);

    /// <summary>Issue a Personal Access Token for a machine user. Returns {tokenId, token, …}. MUTATES + SENSITIVE.</summary>
    public Task<JsonElement> CreatePatAsync(string userId, object body, CancellationToken ct)
        => PostJsonAsync($"management/v1/users/{Esc(userId)}/pats", body, ct);

    /// <summary>Issue a JSON (private) key for a machine user. Response carries the key file (base64)
    /// in <c>keyDetails</c>; the caller writes it host-side and NEVER returns it. MUTATES + SENSITIVE.</summary>
    public Task<JsonElement> CreateMachineKeyAsync(string userId, object body, CancellationToken ct)
        => PostJsonAsync($"management/v1/users/{Esc(userId)}/keys", body, ct);

    // ── HTTP plumbing (mirrors the forge adapters; ZITADEL paths) ──────────────

    private async Task<JsonElement> GetJsonAsync(string path, CancellationToken ct)
    {
        using var resp = await http.GetAsync(path, ct);
        await EnsureOkAsync(resp, ct);
        return await ReadJsonAsync(resp, ct);
    }

    private Task<JsonElement> PostJsonAsync(string path, object body, CancellationToken ct)
        => SendJsonAsync(HttpMethod.Post, path, body, ct);

    private Task<JsonElement> PutJsonAsync(string path, object body, CancellationToken ct)
        => SendJsonAsync(HttpMethod.Put, path, body, ct);

    private async Task<JsonElement> DeleteJsonAsync(string path, CancellationToken ct)
    {
        using var resp = await http.DeleteAsync(path, ct);
        await EnsureOkAsync(resp, ct);
        return await ReadJsonAsync(resp, ct);
    }

    private async Task<JsonElement> SendJsonAsync(HttpMethod method, string path, object body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body, options: J) };
        using var resp = await http.SendAsync(req, ct);
        await EnsureOkAsync(resp, ct);
        return await ReadJsonAsync(resp, ct);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage resp, CancellationToken ct)
    {
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
