using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sinapsi.AgentJwt;

/// <summary>
/// Config for <see cref="AgentJwtMinter"/>. All values are environment-driven
/// with neutral defaults; set them for your own OIDC provider deployment via
/// <see cref="FromEnvironment"/> or by constructing the options directly.
/// </summary>
public sealed class AgentJwtOptions
{
    /// <summary>Dir holding per-agent JWK files (<c>&lt;agent&gt;.json</c>), mounted read-only.</summary>
    public string KeyDir { get; init; } = "/etc/agent-jwt/keys";

    /// <summary>OIDC issuer URL (no trailing slash). Set <c>OIDC_ISSUER</c> for your provider.</summary>
    public string Issuer { get; init; } = "https://oidc.example";

    /// <summary>
    /// The audience project ID; the minted access token is scoped to this project.
    /// The scope template uses the public project-audience URN
    /// (<c>urn:zitadel:iam:org:project:id:&lt;id&gt;:aud</c>, e.g. Zitadel),
    /// an OSS protocol value.
    /// </summary>
    public string AudienceProjectId { get; init; } = "";

    /// <summary>Assertion + cached-token TTL in minutes (cache TTL is this minus 1).</summary>
    public int TtlMinutes { get; init; } = 15;

    public static AgentJwtOptions FromEnvironment() => new()
    {
        KeyDir = Environment.GetEnvironmentVariable("AGENT_KEY_DIR") ?? "/etc/agent-jwt/keys",
        Issuer = Environment.GetEnvironmentVariable("OIDC_ISSUER") ?? "https://oidc.example",
        AudienceProjectId = Environment.GetEnvironmentVariable("OIDC_AUDIENCE_PROJECT_ID") ?? "",
        // Honour JWT_TTL_MIN so callers can override the assertion + cache TTL.
        // Leaving it unset keeps the 15-min default. Only positive values are
        // accepted — a zero/negative value falls back to the default rather than
        // minting an instantly-expired token.
        TtlMinutes = int.TryParse(Environment.GetEnvironmentVariable("JWT_TTL_MIN"), out var t) && t > 0
            ? t
            : 15,
    };
}

/// <summary>
/// RFC 7523 JWT-bearer flow against an OIDC provider (e.g. Zitadel). Loads the
/// agent's JWK (<c>keyId</c> / <c>userId</c> / RSA private PEM), signs an RS256
/// assertion, and exchanges it at the provider's token endpoint for a
/// project-audience access token. Per-agent token cache (TTL-1 minute relative
/// to <see cref="AgentJwtOptions.TtlMinutes"/>). No external JWT library, no
/// shell-out — runs in a plain .NET container.
///
/// Constructor takes the <see cref="HttpClient"/> + options; intended for DI
/// (<c>AddHttpClient&lt;AgentJwtMinter&gt;()</c>). Thread-safe — a
/// <see cref="SemaphoreSlim"/> serialises the cache + refresh path.
/// </summary>
public sealed class AgentJwtMinter(HttpClient http, AgentJwtOptions opt)
{
    private sealed record Jwk(
        [property: JsonPropertyName("keyId")] string KeyId,
        [property: JsonPropertyName("userId")] string UserId,
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("type")] string Type);

    private readonly record struct CachedToken(string Token, DateTimeOffset Expiry);
    private readonly Dictionary<string, CachedToken> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Mint an OIDC access token for <paramref name="agent"/>,
    /// returning a cached one if still fresh. Cache TTL = <c>TtlMinutes - 1</c>
    /// minutes (a 1-minute safety margin against clock skew).</summary>
    public async Task<string> MintAsync(string agent, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(agent, out var c) && c.Expiry > DateTimeOffset.UtcNow)
                return c.Token;
            var token = await MintFreshAsync(agent, ct).ConfigureAwait(false);
            _cache[agent] = new CachedToken(token, DateTimeOffset.UtcNow.AddMinutes(opt.TtlMinutes - 1));
            return token;
        }
        finally { _gate.Release(); }
    }

    private async Task<string> MintFreshAsync(string agent, CancellationToken ct)
    {
        var path = Path.Combine(opt.KeyDir, $"{agent}.json");
        var jwk = JsonSerializer.Deserialize<Jwk>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false))
                  ?? throw new InvalidOperationException($"JWK parse failed for {agent}");

        var assertion = BuildAssertion(jwk);
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
            ["assertion"] = assertion,
            ["scope"] = $"openid urn:zitadel:iam:org:project:id:{opt.AudienceProjectId}:aud",
        };

        using var res = await http.PostAsync(
            new Uri($"{opt.Issuer}/oauth/v2/token"),
            new FormUrlEncodedContent(form), ct).ConfigureAwait(false);
        var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"OIDC token HTTP {(int)res.StatusCode}: {body[..Math.Min(300, body.Length)]}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("access_token").GetString()
               ?? throw new InvalidOperationException("no access_token in OIDC response");
    }

    private string BuildAssertion(Jwk jwk)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = new { alg = "RS256", kid = jwk.KeyId, typ = "JWT" };
        var payload = new
        {
            iss = jwk.UserId,
            sub = jwk.UserId,
            aud = opt.Issuer,
            exp = now + opt.TtlMinutes * 60,
            iat = now,
        };
        var signingInput = $"{B64Url(JsonSerializer.SerializeToUtf8Bytes(header))}.{B64Url(JsonSerializer.SerializeToUtf8Bytes(payload))}";
        using var rsa = RSA.Create();
        rsa.ImportFromPem(jwk.Key);
        var sig = rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{B64Url(sig)}";
    }

    private static string B64Url(byte[] b) =>
        Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
