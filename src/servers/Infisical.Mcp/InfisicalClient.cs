using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Infisical.Mcp;

/// <summary>
/// Thin Infisical REST client (Universal-Auth login -> token -> folders/secrets API).
/// Secret VALUES only flow through here transiently (generate -> store); never logged.
/// </summary>
public sealed class InfisicalClient(HttpClient http, InfisicalOptions opt, ILogger<InfisicalClient> log)
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private string Api => $"{opt.HostUrl}/api";
    private string? _token;
    private DateTimeOffset _exp;

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _exp) return _token;
        using var res = await http.PostAsJsonAsync($"{Api}/v1/auth/universal-auth/login",
            new { clientId = opt.ClientId, clientSecret = opt.ClientSecret }, _json, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>(_json, ct).ConfigureAwait(false);
        // Guard the whole shape: a login response that omits accessToken (or carries it
        // as JSON null / a non-string) must surface a clear error, not a generic
        // KeyNotFoundException from GetProperty on an absent key.
        _token = doc.TryGetProperty("accessToken", out var at) && at.ValueKind == JsonValueKind.String
                 ? at.GetString()!
                 : throw new InvalidOperationException("infisical login: no accessToken");
        _exp = DateTimeOffset.UtcNow.AddMinutes(50);
        log.LogInformation("infisical universal-auth login ok");
        return _token;
    }

    private HttpRequestMessage Json(HttpMethod m, string path, object body, string token)
    {
        var r = new HttpRequestMessage(m, $"{Api}{path}") { Content = JsonContent.Create(body, options: _json) };
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return r;
    }

    /// <summary>Create the group folder then the service folder under it (idempotent — an
    /// already-existing folder returns a 4xx we tolerate).</summary>
    public async Task EnsureFolderAsync(string group, string service, CancellationToken ct)
    {
        var token = await TokenAsync(ct).ConfigureAwait(false);
        foreach (var (name, parent) in new[] { (group, "/"), (service, $"/{group}") })
        {
            using var req = Json(HttpMethod.Post, "/v1/folders",
                new { workspaceId = opt.ProjectId, environment = opt.EnvName, name, path = parent }, token);
            using var _ = await http.SendAsync(req, ct).ConfigureAwait(false); // tolerate exists
        }
    }

    /// <summary>Upsert a secret value at <paramref name="secretPath"/> (POST create; PATCH on conflict).</summary>
    public async Task SetSecretAsync(string secretPath, string name, string value, CancellationToken ct)
    {
        var token = await TokenAsync(ct).ConfigureAwait(false);
        var body = new { workspaceId = opt.ProjectId, environment = opt.EnvName, secretPath, secretValue = value };
        using var post = Json(HttpMethod.Post, $"/v3/secrets/raw/{name}", body, token);
        using var pres = await http.SendAsync(post, ct).ConfigureAwait(false);
        if (pres.IsSuccessStatusCode) return;
        using var patch = Json(HttpMethod.Patch, $"/v3/secrets/raw/{name}", body, token);
        using var ures = await http.SendAsync(patch, ct).ConfigureAwait(false);
        if (!ures.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"set secret {secretPath}/{name}: POST {(int)pres.StatusCode}, PATCH {(int)ures.StatusCode}");
    }

    /// <summary>Secret NAMES (never values) at a path.</summary>
    public async Task<IReadOnlyList<string>> ListSecretNamesAsync(string secretPath, CancellationToken ct)
    {
        var token = await TokenAsync(ct).ConfigureAwait(false);
        var url = $"{Api}/v3/secrets/raw?workspaceId={Uri.EscapeDataString(opt.ProjectId)}"
                  + $"&environment={Uri.EscapeDataString(opt.EnvName)}&secretPath={Uri.EscapeDataString(secretPath)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>(_json, ct).ConfigureAwait(false);
        var names = new List<string>();
        if (doc.TryGetProperty("secrets", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var s in arr.EnumerateArray())
                if (s.TryGetProperty("secretKey", out var k) && k.GetString() is { } n) names.Add(n);
        return names;
    }
}
