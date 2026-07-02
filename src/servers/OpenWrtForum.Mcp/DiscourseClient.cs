using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenWrtForum.Mcp;

/// <summary>
/// Long-lived HTTP client for the Discourse REST API. Holds a single
/// <see cref="HttpClient"/> + <see cref="CookieContainer"/> and a one-shot
/// CSRF/login flow guarded by a <see cref="SemaphoreSlim"/>. Singleton-scoped.
/// </summary>
public sealed class DiscourseClient : IDisposable
{
    private readonly DiscourseOptions _opts;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private bool _authenticated;
    private const string UA =
        "Mozilla/5.0 (compatible; openwrt-forum-mcp/1.0)";

    public DiscourseClient(DiscourseOptions opts)
        : this(opts, new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true })
    {
    }

    /// <summary>Test/extension seam: supply a custom message handler (e.g. a stub
    /// that returns canned Discourse responses) so the GET/shape path is unit-testable
    /// without a live forum.</summary>
    public DiscourseClient(DiscourseOptions opts, HttpMessageHandler handler)
    {
        _opts = opts;
        // Timeout is bound + clamped in DiscourseOptions (fail-closed on bad
        // config); apply it here so a hung/slow forum cannot wedge a request.
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(opts.HttpTimeoutMs) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UA);
    }

    public string BaseUrl => _opts.Url;

    public async Task<JsonNode> GetAsync(string path, IDictionary<string, string?>? query = null, CancellationToken ct = default)
    {
        var url = BuildUrl(path, query);
        using var res = await _http.GetAsync(url, ct);
        await EnsureSuccessOrThrowStructuredAsync(res, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        return JsonNode.Parse(body) ?? new JsonObject();
    }

    public async Task<JsonNode> PostAuthAsync(string path, JsonObject body, CancellationToken ct = default)
    {
        await EnsureAuthenticatedAsync(ct);
        var csrf = await CsrfTokenAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}{path}")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-CSRF-Token", csrf);
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        using var res = await _http.SendAsync(req, ct);
        await EnsureSuccessOrThrowStructuredAsync(res, ct);
        var s = await res.Content.ReadAsStringAsync(ct);
        return JsonNode.Parse(s) ?? new JsonObject();
    }

    public async Task<JsonNode> PutAuthAsync(string path, JsonObject? body = null, CancellationToken ct = default)
    {
        await EnsureAuthenticatedAsync(ct);
        var csrf = await CsrfTokenAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Put, $"{BaseUrl}{path}")
        {
            Content = new StringContent((body ?? new JsonObject()).ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-CSRF-Token", csrf);
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        using var res = await _http.SendAsync(req, ct);
        await EnsureSuccessOrThrowStructuredAsync(res, ct);
        var s = await res.Content.ReadAsStringAsync(ct);
        return JsonNode.Parse(s) ?? new JsonObject();
    }

    public async Task EnsureAuthenticatedAsync(CancellationToken ct)
    {
        if (_authenticated) return;
        if (!_opts.HasCredentials) return; // read-only mode

        await _authLock.WaitAsync(ct);
        try
        {
            if (_authenticated) return;
            var csrf = await CsrfTokenAsync(ct);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/session")
            {
                Content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("login", _opts.Username),
                    new KeyValuePair<string, string>("password", _opts.Password),
                }),
            };
            req.Headers.Add("X-CSRF-Token", csrf);
            using var res = await _http.SendAsync(req, ct);
            await EnsureSuccessOrThrowStructuredAsync(res, ct);
            var body = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
            if (body?["error"] is not null || (body?["failed"]?.GetValueKind() == JsonValueKind.True))
                throw new InvalidOperationException($"Login failed: {body?["error"]?.ToString() ?? body?.ToString() ?? "unknown"}");
            _authenticated = true;
        }
        finally
        {
            _authLock.Release();
        }
    }

    private async Task<string> CsrfTokenAsync(CancellationToken ct)
    {
        using var res = await _http.GetAsync($"{BaseUrl}/session/csrf.json", ct);
        await EnsureSuccessOrThrowStructuredAsync(res, ct);
        var body = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
        return body?["csrf"]?.GetValue<string>() ?? throw new InvalidOperationException("CSRF token missing");
    }

    /// <summary>Reads the response body on failure to surface a structured
    /// <c>{error, status_code, body}</c> envelope (body is JSON-parsed if
    /// possible, else the first 500 chars of text). On 401/403 it also resets
    /// the auth flag under the lock so the next call re-logs-in transparently.</summary>
    private async Task EnsureSuccessOrThrowStructuredAsync(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;
        var status = (int)res.StatusCode;

        // Reset auth on session-loss codes. Take the lock to avoid racing a
        // concurrent EnsureAuthenticatedAsync that already won.
        if (status == 401 || status == 403)
        {
            await _authLock.WaitAsync(ct);
            try { _authenticated = false; }
            finally { _authLock.Release(); }
        }

        string body;
        try { body = await res.Content.ReadAsStringAsync(ct); }
        catch { body = ""; }

        // Fail safe: scrub any key/credential material out of the upstream body
        // BEFORE it is placed in the envelope that becomes the caller-facing error
        // message. A verbose Discourse error that echoed the account password, a
        // session token, or a pasted key can never reach a caller. Sanitize also
        // length-caps, so a pathological body cannot blow up the response; the
        // subsequent 500-char clamp is a further belt-and-braces bound.
        body = OpenWrtForumErrors.Sanitize(body);

        JsonNode? parsed = null;
        try { parsed = JsonNode.Parse(body); } catch { /* leave null */ }

        var envelope = new JsonObject
        {
            ["error"] = $"discourse {status}",
            ["status_code"] = status,
            ["body"] = parsed is not null
                ? parsed
                : (JsonNode)(body.Length <= 500 ? body : body[..500] + "…"),
        };
        throw new InvalidOperationException(envelope.ToJsonString());
    }

    private string BuildUrl(string path, IDictionary<string, string?>? query)
    {
        var url = $"{BaseUrl}{path}";
        if (query is null || query.Count == 0) return url;
        var qs = string.Join("&", query
            .Where(kv => kv.Value is not null)
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}"));
        return string.IsNullOrEmpty(qs) ? url : $"{url}?{qs}";
    }

    public void Dispose()
    {
        _http.Dispose();
        _authLock.Dispose();
    }
}
