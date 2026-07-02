using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Zitadel.Mcp.Api;

namespace Zitadel.Mcp.Auth;

/// <summary>
/// Mints + caches ZITADEL access tokens via the RFC 7523 JWT-bearer flow (used in SA-key mode).
///
/// <para>Auth model:</para>
/// <list type="number">
///   <item>Load the service account from <see cref="ZitadelConfig.SaKeyFile"/>.</item>
///   <item>Self-sign an RS256 JWT (kid=KeyId, iss=sub=UserId, aud=Issuer, exp=now+1h).</item>
///   <item>POST to <c>{ApiBase}/oauth/v2/token</c> with <c>grant_type=jwt-bearer</c> + the
///     assertion + scope. A per-request <c>Host</c> header + <c>X-Forwarded-Proto: https</c> are
///     sent so ZITADEL sees the public host even when the API root is a LAN-bypass origin (the
///     issuer claim is validated against the host, not the wire authority).</item>
///   <item>Cache the access token until <c>exp - 60s</c>.</item>
/// </list>
///
/// <para>Security: the SA private key + the cached access token are NEVER surfaced in exception
/// messages or log lines. Token-exchange errors surface only ZITADEL's response body (no JWT, no
/// key material), scrubbed by the tool guard before reaching a caller. Logging emits only the SA
/// <c>keyId</c> — never the <c>userId</c>, the cached token, the assertion, or the response body.
/// </para>
///
/// <para>Thread-safety: a <see cref="SemaphoreSlim"/> serialises the cache + refresh path.</para>
/// </summary>
public sealed class JwtBearerTokenProvider(HttpClient http, ZitadelConfig cfg, ILogger<JwtBearerTokenProvider> log)
{
    private string? _cachedToken;
    private DateTimeOffset _cachedExpiry;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Scope string sent on token exchange: the reserved ZITADEL project-audience scope
    /// plus <c>openid profile</c>, so the minted token carries the standard project audience.</summary>
    private const string ScopeString =
        "openid profile urn:zitadel:iam:org:project:id:zitadel:aud";

    /// <summary>
    /// Get a valid access token, refreshing if the cached one is within 60s of expiry.
    /// </summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cachedToken is not null
                && _cachedExpiry - TimeSpan.FromSeconds(60) > DateTimeOffset.UtcNow)
            {
                return _cachedToken;
            }

            var sa = ServiceAccount.LoadFromFile(cfg.SaKeyFile!);
            log.LogInformation("ZITADEL token refresh start (keyId={KeyId})", sa.KeyId);
            var assertion = BuildAssertion(sa);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (token, expiresIn) = await ExchangeAsync(assertion, ct).ConfigureAwait(false);
            sw.Stop();

            _cachedToken = token;
            _cachedExpiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
            log.LogInformation(
                "ZITADEL token refresh done (keyId={KeyId}, expiresIn={ExpiresIn}s, took={Ms}ms)",
                sa.KeyId, expiresIn, sw.ElapsedMilliseconds);
            return token;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// RS256-sign a JWT assertion with the SA's RSA private key (iss=sub=userId, aud=issuer,
    /// exp=now+3600). The SA private key never leaves this method's stack — it is imported into a
    /// transient RSA, used to sign, then disposed. No log line references it.
    /// </summary>
    private string BuildAssertion(ServiceAccount sa)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = new { alg = "RS256", kid = sa.KeyId, typ = "JWT" };
        var payload = new
        {
            iss = sa.UserId,
            sub = sa.UserId,
            aud = cfg.Issuer,
            iat = now,
            exp = now + 3600,
        };

        var signingInput =
            $"{B64Url(JsonSerializer.SerializeToUtf8Bytes(header))}." +
            $"{B64Url(JsonSerializer.SerializeToUtf8Bytes(payload))}";

        using var rsa = RSA.Create();
        rsa.ImportFromPem(sa.PrivateKeyPem);
        var sig = rsa.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return $"{signingInput}.{B64Url(sig)}";
    }

    /// <summary>
    /// POST to <c>{ApiBase}/oauth/v2/token</c> with mandatory <c>Host</c> + <c>X-Forwarded-Proto:
    /// https</c> headers, under a per-call deadline.
    /// </summary>
    private async Task<(string token, int expiresIn)> ExchangeAsync(string assertion, CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromMilliseconds(cfg.HttpTimeoutMs));

        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri($"{cfg.BaseUrl}/oauth/v2/token"));
        // Per-request Host header overrides the wire-level Host without changing the authority the
        // connection pool is keyed on — the canonical LAN-bypass shape (auto-redirect is off at
        // HttpClient construction so a 30x can't drop this override).
        req.Headers.Host = cfg.HostHeader;
        req.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
            ["scope"]      = ScopeString,
            ["assertion"]  = assertion,
        });

        using var res = await http.SendAsync(req, linkedCts.Token).ConfigureAwait(false);
        var body = await res.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);

        if (!res.IsSuccessStatusCode)
        {
            // Log path + status only (NOT the body — it could echo caller-supplied input). Surface
            // only ZITADEL's body to the caller (never the assertion / signed JWT); the tool guard
            // scrubs it of any credential material before it leaves the process.
            log.LogWarning("ZITADEL token-exchange {Status} at /oauth/v2/token", (int)res.StatusCode);
            throw new ZitadelApiException(
                (int)res.StatusCode,
                $"{(int)res.StatusCode} token-exchange failed at /oauth/v2/token: " +
                (body.Length > 600 ? body[..600] + "…" : body));
        }

        using var doc = JsonDocument.Parse(body);
        var token = doc.RootElement.GetProperty("access_token").GetString()
                    ?? throw new InvalidOperationException("no access_token in ZITADEL token response");
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var eiv)
            ? eiv
            : 3600;
        return (token, expiresIn);
    }

    private static string B64Url(byte[] b) =>
        Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
