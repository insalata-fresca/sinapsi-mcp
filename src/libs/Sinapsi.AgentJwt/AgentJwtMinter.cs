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
    public int TtlMinutes { get; init; } = DefaultTtlMinutes;

    /// <summary>Default assertion + cache TTL (minutes) when none is configured.</summary>
    public const int DefaultTtlMinutes = 15;

    /// <summary>Lower bound on a configurable TTL. The cache margin is TTL-1, so a
    /// TTL below 2 would produce a non-positive cache window; 2 is the floor.</summary>
    public const int MinTtlMinutes = 2;

    /// <summary>Upper bound on a configurable TTL. A machine assertion is
    /// short-lived by design; 1440 minutes (24 h) is a generous ceiling. A larger
    /// value is treated as a config error, not silently honoured.</summary>
    public const int MaxTtlMinutes = 1_440;

    public static AgentJwtOptions FromEnvironment() => new()
    {
        KeyDir = Environment.GetEnvironmentVariable("AGENT_KEY_DIR") ?? "/etc/agent-jwt/keys",
        Issuer = Environment.GetEnvironmentVariable("OIDC_ISSUER") ?? "https://oidc.example",
        AudienceProjectId = Environment.GetEnvironmentVariable("OIDC_AUDIENCE_PROJECT_ID") ?? "",
        TtlMinutes = ReadTtlMinutes(),
    };

    /// <summary>
    /// Read + fail-closed-validate <c>JWT_TTL_MIN</c>. Unset -> the 15-minute
    /// default. A non-numeric or out-of-range value (below
    /// <see cref="MinTtlMinutes"/> or above <see cref="MaxTtlMinutes"/>) THROWS an
    /// <see cref="InvalidOperationException"/> naming the offending env var, rather
    /// than silently swallowing a footgun into an instantly-expired (or absurdly
    /// long-lived) token. A bare <c>0</c>/<c>-5</c> now surfaces the misconfig.
    /// </summary>
    private static int ReadTtlMinutes()
    {
        var raw = Environment.GetEnvironmentVariable("JWT_TTL_MIN");
        if (string.IsNullOrEmpty(raw))
            return DefaultTtlMinutes;

        if (!int.TryParse(raw, out var t) || t < MinTtlMinutes || t > MaxTtlMinutes)
            throw new InvalidOperationException(
                $"JWT_TTL_MIN='{raw}' is invalid: expected an integer in " +
                $"{MinTtlMinutes}..{MaxTtlMinutes} minutes (default {DefaultTtlMinutes}).");

        return t;
    }
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
    /// minutes (a 1-minute safety margin against clock skew).
    /// <para>
    /// The <paramref name="agent"/> name becomes a filesystem path component
    /// (<c>&lt;KeyDir&gt;/&lt;agent&gt;.json</c>), so it is validated first: a
    /// missing/over-long name, a path separator, traversal (<c>.</c>/<c>..</c>),
    /// NUL, or a control character is rejected with an <see cref="ArgumentException"/>
    /// BEFORE any cache lookup or filesystem access — a hostile name can never
    /// escape <see cref="AgentJwtOptions.KeyDir"/>.
    /// </para></summary>
    /// <exception cref="ArgumentException">The agent name is malformed.</exception>
    public async Task<string> MintAsync(string agent, CancellationToken ct)
    {
        if (AgentJwtValidation.ValidateAgent(agent) is { } reason)
            throw new ArgumentException(AgentJwtErrors.Sanitize(reason), nameof(agent));

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
        // Fail-closed on unusable config at the seam where it is first used, so a
        // missing issuer/audience or a non-URL issuer throws an ArgumentException
        // naming the offending option instead of building a request against a
        // footgun default. (Validating here, not in the ctor, keeps DI-time
        // construction cheap — the exemplar validates config at bind time too.)
        AgentJwtValidation.ValidateOptions(opt);

        var path = Path.Combine(opt.KeyDir, $"{agent}.json");
        var jwk = JsonSerializer.Deserialize<Jwk>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false))
                  ?? throw new InvalidOperationException($"JWK parse failed for {agent}");

        var assertion = BuildAssertion(agent, jwk);
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
            // Sanitize the provider body before surfacing it: a token endpoint
            // could echo the assertion / a bearer token / a key on an error, and
            // that must never travel out in an exception message.
            throw new InvalidOperationException(
                $"OIDC token HTTP {(int)res.StatusCode}: {AgentJwtErrors.Sanitize(body[..Math.Min(300, body.Length)])}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("access_token").GetString()
               ?? throw new InvalidOperationException("no access_token in OIDC response");
    }

    private string BuildAssertion(string agent, Jwk jwk)
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
        try
        {
            rsa.ImportFromPem(jwk.Key);
        }
        catch (Exception e)
        {
            // A malformed private key can make ImportFromPem throw a message that
            // may quote the offending PEM. Never let the signing key travel out in
            // the surfaced error: replace it with a neutral, agent-scoped message
            // and route it through Sanitize as defence in depth.
            throw new InvalidOperationException(
                AgentJwtErrors.Sanitize($"invalid signing key for agent '{agent}': {e.GetType().Name}"));
        }
        var sig = rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{B64Url(sig)}";
    }

    private static string B64Url(byte[] b) =>
        Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
