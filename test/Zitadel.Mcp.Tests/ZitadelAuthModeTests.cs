using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Zitadel.Mcp;
using Zitadel.Mcp.Auth;

namespace Zitadel.Mcp.Tests;

/// <summary>
/// The Zitadel.Mcp host authenticates in one of two env-selected modes:
///   • SA-key mode when <c>ZITADEL_SA_KEY_FILE</c> is set — the host loads a service-account JSON
///     key and self-mints a short-lived JWT bearer via the RFC 7523 jwt-bearer grant.
///   • PAT mode when only <c>ZITADEL_TOKEN</c> is set — the host attaches that static bearer.
/// These tests pin the fail-closed mode selection and exercise the JWT-mint path with a fake key
/// and a scripted token endpoint (no live instance, no real credential).
/// </summary>
[Collection(EnvSensitiveCollection.Name)]
public sealed class ZitadelAuthModeTests
{
    private static T WithEnv<T>(IReadOnlyDictionary<string, string?> env, Func<T> body)
    {
        var keys = new[]
        {
            "ZITADEL_BASE_URL", "ZITADEL_API_URL", "ZITADEL_TOKEN", "ZITADEL_SA_KEY_FILE",
            "ZITADEL_ISSUER", "ZITADEL_HOST_HEADER", "ZITADEL_MCP_PORT", "ZITADEL_HTTP_TIMEOUT_MS",
            "AGENT_KEY_DIR",
        };
        var saved = keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var k in keys) Environment.SetEnvironmentVariable(k, env.TryGetValue(k, out var v) ? v : null);
            return body();
        }
        finally
        {
            foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v);
        }
    }

    // ── Mode selection ─────────────────────────────────────────────────────────

    [Fact]
    public void Sa_key_mode_is_selected_when_sa_key_file_is_set()
    {
        var cfg = WithEnv(new Dictionary<string, string?>
        {
            ["ZITADEL_BASE_URL"] = "https://auth.example.com",
            ["ZITADEL_SA_KEY_FILE"] = "/etc/mcp-gateway/zitadel.json",
        }, ZitadelConfig.FromEnv);

        Assert.Equal(ZitadelAuthMode.ServiceAccountKey, cfg.AuthMode);
        Assert.Equal("/etc/mcp-gateway/zitadel.json", cfg.SaKeyFile);
        Assert.Null(cfg.Token);
        // Issuer + host header default off the API root when not set explicitly.
        Assert.Equal("https://auth.example.com", cfg.Issuer);
        Assert.Equal("auth.example.com", cfg.HostHeader);
    }

    [Fact]
    public void Sa_key_mode_wins_even_when_a_static_token_is_also_present()
    {
        // The stronger self-minted model takes precedence — a stray static token does not
        // silently override it.
        var cfg = WithEnv(new Dictionary<string, string?>
        {
            ["ZITADEL_BASE_URL"] = "https://auth.example.com",
            ["ZITADEL_SA_KEY_FILE"] = "/etc/mcp-gateway/zitadel.json",
            ["ZITADEL_TOKEN"] = "a-static-token",
        }, ZitadelConfig.FromEnv);

        Assert.Equal(ZitadelAuthMode.ServiceAccountKey, cfg.AuthMode);
    }

    [Fact]
    public void Api_url_alias_is_accepted_as_the_base_and_issuer_host_header_can_be_overridden()
    {
        // The live SA-key deploy provides a LAN-bypass API_URL + an explicit issuer + host header.
        var cfg = WithEnv(new Dictionary<string, string?>
        {
            ["ZITADEL_API_URL"] = "http://10.0.0.9:80",
            ["ZITADEL_ISSUER"] = "https://auth.example.com",
            ["ZITADEL_HOST_HEADER"] = "auth.example.com",
            ["ZITADEL_SA_KEY_FILE"] = "/etc/mcp-gateway/zitadel.json",
        }, ZitadelConfig.FromEnv);

        Assert.Equal("http://10.0.0.9:80", cfg.BaseUrl);
        Assert.Equal("https://auth.example.com", cfg.Issuer);
        Assert.Equal("auth.example.com", cfg.HostHeader);
        Assert.Equal(ZitadelAuthMode.ServiceAccountKey, cfg.AuthMode);
    }

    [Fact]
    public void Pat_mode_is_selected_when_only_a_static_token_is_set()
    {
        var cfg = WithEnv(new Dictionary<string, string?>
        {
            ["ZITADEL_BASE_URL"] = "https://auth.example.com",
            ["ZITADEL_TOKEN"] = "svc-token",
        }, ZitadelConfig.FromEnv);

        Assert.Equal(ZitadelAuthMode.Pat, cfg.AuthMode);
        Assert.Equal("svc-token", cfg.Token);
        Assert.Null(cfg.SaKeyFile);
    }

    [Fact]
    public void No_auth_configured_fails_closed_naming_both_paths()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WithEnv(new Dictionary<string, string?>
            {
                ["ZITADEL_BASE_URL"] = "https://auth.example.com",
                // neither ZITADEL_SA_KEY_FILE nor ZITADEL_TOKEN
            }, ZitadelConfig.FromEnv));

        Assert.Contains("ZITADEL_SA_KEY_FILE", ex.Message);
        Assert.Contains("ZITADEL_TOKEN", ex.Message);
    }

    // ── SA-key JWT-mint path ───────────────────────────────────────────────────

    /// <summary>Write a fake SA JSON key (real RSA private key, fake ids) to a temp file and
    /// return its path. The RSA key is genuine so the mint's RS256 signature is well-formed, but
    /// the userId/keyId are throwaway — no real ZITADEL account.</summary>
    private static string WriteFakeSaKey(out string dir)
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportPkcs8PrivateKeyPem();
        dir = Path.Combine(Path.GetTempPath(), "zitadel-sakey-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "zitadel.json");
        var sa = new { type = "serviceaccount", keyId = "key-123", key = pem, userId = "user-456" };
        File.WriteAllText(path, JsonSerializer.Serialize(sa));
        return path;
    }

    private sealed class TokenEndpointHandler : HttpMessageHandler
    {
        public readonly List<(string Path, string? Host, string? ForwardedProto, string? Body)> Calls = new();
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public TokenEndpointHandler(HttpStatusCode status, string body) { _status = status; _body = body; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var reqBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((
                request.RequestUri!.AbsolutePath,
                request.Headers.Host,
                request.Headers.TryGetValues("X-Forwarded-Proto", out var xfp) ? string.Join(",", xfp) : null,
                reqBody));
            return new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") };
        }
    }

    private static JwtBearerTokenProvider BuildProvider(ZitadelConfig cfg, TokenEndpointHandler handler)
    {
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return new JwtBearerTokenProvider(http, cfg, NullLogger<JwtBearerTokenProvider>.Instance);
    }

    [Fact]
    public async Task Sa_key_mode_mints_a_jwt_and_posts_the_jwt_bearer_grant_with_host_headers()
    {
        var keyPath = WriteFakeSaKey(out var dir);
        try
        {
            var cfg = WithEnv(new Dictionary<string, string?>
            {
                ["ZITADEL_API_URL"] = "http://10.0.0.9:80",
                ["ZITADEL_ISSUER"] = "https://auth.example.com",
                ["ZITADEL_HOST_HEADER"] = "auth.example.com",
                ["ZITADEL_SA_KEY_FILE"] = keyPath,
            }, ZitadelConfig.FromEnv);

            var handler = new TokenEndpointHandler(HttpStatusCode.OK, """{"access_token":"minted-jwt-abc","expires_in":3600}""");
            var provider = BuildProvider(cfg, handler);

            var token = await provider.GetAccessTokenAsync(CancellationToken.None);

            Assert.Equal("minted-jwt-abc", token);
            var call = Assert.Single(handler.Calls);
            Assert.Equal("/oauth/v2/token", call.Path);
            // ZITADEL needs the public host + X-Forwarded-Proto over a LAN-bypass API origin.
            Assert.Equal("auth.example.com", call.Host);
            Assert.Equal("https", call.ForwardedProto);
            // RFC 7523 jwt-bearer grant + a well-formed three-part assertion the SA key signed.
            Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Ajwt-bearer", call.Body);
            Assert.Contains("assertion=", call.Body);
            var assertion = ExtractAssertion(call.Body!);
            Assert.Equal(3, assertion.Split('.').Length);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Sa_key_mode_caches_the_token_across_calls()
    {
        var keyPath = WriteFakeSaKey(out var dir);
        try
        {
            var cfg = WithEnv(new Dictionary<string, string?>
            {
                ["ZITADEL_BASE_URL"] = "https://auth.example.com",
                ["ZITADEL_SA_KEY_FILE"] = keyPath,
            }, ZitadelConfig.FromEnv);

            var handler = new TokenEndpointHandler(HttpStatusCode.OK, """{"access_token":"cached-jwt","expires_in":3600}""");
            var provider = BuildProvider(cfg, handler);

            var t1 = await provider.GetAccessTokenAsync(CancellationToken.None);
            var t2 = await provider.GetAccessTokenAsync(CancellationToken.None);

            Assert.Equal("cached-jwt", t1);
            Assert.Equal(t1, t2);
            // Only ONE mint call — the second read is served from cache (expiry far in the future).
            Assert.Single(handler.Calls);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Sa_key_mint_surfaces_a_non_2xx_token_exchange_as_a_ZitadelApiException()
    {
        var keyPath = WriteFakeSaKey(out var dir);
        try
        {
            var cfg = WithEnv(new Dictionary<string, string?>
            {
                ["ZITADEL_BASE_URL"] = "https://auth.example.com",
                ["ZITADEL_SA_KEY_FILE"] = keyPath,
            }, ZitadelConfig.FromEnv);

            var handler = new TokenEndpointHandler(HttpStatusCode.Unauthorized, """{"error":"invalid_grant"}""");
            var provider = BuildProvider(cfg, handler);

            var ex = await Assert.ThrowsAsync<Zitadel.Mcp.Api.ZitadelApiException>(
                () => provider.GetAccessTokenAsync(CancellationToken.None));

            Assert.Equal(401, ex.Status);
            Assert.Contains("invalid_grant", ex.Message);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Missing_sa_key_file_throws_without_leaking_content()
    {
        var missing = Path.Combine(Path.GetTempPath(), "no-such-sa-" + Guid.NewGuid().ToString("N") + ".json");
        var ex = Assert.Throws<InvalidOperationException>(() => ServiceAccount.LoadFromFile(missing));
        Assert.Contains(missing, ex.Message);
        Assert.Contains("service-account key not found", ex.Message);
    }

    [Fact]
    public void Malformed_sa_key_throws_without_echoing_the_body()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zitadel-sakey-bad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "zitadel.json");
        try
        {
            // Missing the required "key" field.
            File.WriteAllText(path, """{"type":"serviceaccount","keyId":"k","userId":"u"}""");
            var ex = Assert.Throws<InvalidOperationException>(() => ServiceAccount.LoadFromFile(path));
            Assert.Contains("missing required fields", ex.Message);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static string ExtractAssertion(string formBody)
    {
        foreach (var pair in formBody.Split('&'))
        {
            var i = pair.IndexOf('=');
            if (i > 0 && pair[..i] == "assertion")
                return Uri.UnescapeDataString(pair[(i + 1)..]);
        }
        throw new InvalidOperationException("no assertion in form body");
    }
}
