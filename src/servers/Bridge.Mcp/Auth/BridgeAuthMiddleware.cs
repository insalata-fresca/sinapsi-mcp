using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;

namespace Bridge.Mcp.Auth;

/// <summary>
/// ASP.NET Core middleware that authenticates every request against the bridge's
/// two-path auth model (ported from Python auth.py):
///
/// 1. Legacy static bearer (constant-time compare) — grants LEGACY_SCOPES.
/// 2. Zitadel JWT (RS256/ES256; aud in {MCP_RESOURCE_URI, ZITADEL_CLIENT_ID};
///    iss == ZITADEL_ISSUER; scopes from scope/scp) — grants token scopes | LEGACY_SCOPES.
///
/// On success the <see cref="BridgeAuthContext"/> is stored in an AsyncLocal accessible
/// to tools via <see cref="BridgeAuthState.CurrentAuth"/>. The source IP is captured
/// via X-Forwarded-For (set by the NPM proxy) or the direct client IP.
///
/// Public paths (/health, /.well-known/oauth-protected-resource) bypass auth.
///
/// Parity guarantees vs Python auth.py:
///   - iat claim is REQUIRED (options={'require':['exp','iat','iss','aud']}).
///   - ClockSkew is zero (PyJWT default leeway=0).
///   - JWKS/OIDC fetch failure → 401 + WWW-Authenticate, NOT 500.
/// </summary>
public sealed class BridgeAuthMiddleware(
    RequestDelegate next,
    BridgeConfig config,
    ILogger<BridgeAuthMiddleware> logger)
{
    private static readonly HashSet<string> PublicPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/health",
        "/.well-known/oauth-protected-resource",
    };

    // JWKS cache — refresh once per hour (matches Python's PyJWKClient lifespan).
    // The bridge targets a single Zitadel host, so a shared static HttpClient is
    // appropriate here and avoids per-request socket exhaustion.
    private static readonly HttpClient JwksHttpClient = new();
    private readonly SemaphoreSlim _jwksLock = new(1, 1);
    private IEnumerable<SecurityKey>? _cachedSigningKeys;
    private DateTimeOffset _jwksLoadedAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan JwksRefreshInterval = TimeSpan.FromHours(1);

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";

        // Capture source IP (X-Forwarded-For from NPM proxy; fall back to direct client).
        var xff = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        var sourceIp = xff is { Length: > 0 }
            ? xff.Split(',')[0].Trim()
            : context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        BridgeAuthState.CurrentSourceIp = sourceIp;

        try
        {
            if (PublicPaths.Contains(path))
            {
                await next(context);
                return;
            }

            var authorization = context.Request.Headers.Authorization.FirstOrDefault();
            var token = ExtractBearer(authorization);
            if (token is null)
            {
                await WriteMissingAuthAsync(context, config);
                return;
            }

            BridgeAuthContext? authCtx = null;
            // Try JWT first (three dot-separated segments).
            if (token.Count(c => c == '.') == 2)
            {
                try
                {
                    authCtx = await ValidateJwtAsync(token, context.RequestAborted);
                }
                catch (SecurityTokenException ex)
                {
                    // Token looks like a JWT but failed validation — surface the JWT error.
                    logger.LogInformation("JWT validation rejected: {Message}", ex.Message);
                    await WriteInvalidTokenAsync(context, config, "invalid_token");
                    return;
                }
                catch (Exception ex)
                {
                    // JWKS/OIDC metadata fetch failure (HttpRequestException, InvalidOperationException,
                    // etc.) — Python maps these to AuthError('jwks_unavailable') → 401 server_error.
                    // We do the same: fail closed with 401, NOT 500.
                    logger.LogWarning("JWKS fetch or OIDC config failure: {Message}", ex.Message);
                    await WriteInvalidTokenAsync(context, config, "server_error");
                    return;
                }
            }

            // Not a JWT (or JWT validation yielded null) — try legacy bearer.
            authCtx ??= ValidateLegacyBearer(token);

            if (authCtx is null)
            {
                await WriteInvalidTokenAsync(context, config, "invalid_token");
                return;
            }

            BridgeAuthState.CurrentAuth = authCtx;
            try
            {
                await next(context);
            }
            finally
            {
                BridgeAuthState.CurrentAuth = null;
            }
        }
        finally
        {
            BridgeAuthState.CurrentSourceIp = "unknown";
        }
    }

    // ── Bearer extraction ────────────────────────────────────────────────────

    internal static string? ExtractBearer(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        var parts = header.Trim().Split(null, 2);
        if (parts.Length != 2 || !parts[0].Equals("Bearer", StringComparison.OrdinalIgnoreCase))
            return null;
        return parts[1].Trim();
    }

    // ── Legacy bearer (constant-time compare) ────────────────────────────────

    private BridgeAuthContext? ValidateLegacyBearer(string token)
    {
        var expected = config.BridgeBearerToken;
        if (string.IsNullOrEmpty(expected)) return null;
        // Constant-time comparison (matches Python hmac.compare_digest).
        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(token),
            Encoding.UTF8.GetBytes(expected)))
            return null;

        return new BridgeAuthContext
        {
            Mode     = "bearer",
            Subject  = "legacy-bearer",
            // Auto-granted set = LegacyScopes.All (+ cervello scopes iff CERVELLO_EXPOSED=true).
            Scopes   = new HashSet<string>(LegacyScopes.Granted(config.CervelloExposed), StringComparer.Ordinal),
            RawToken = token,
        };
    }

    // ── Zitadel JWT (RS256/ES256) ─────────────────────────────────────────────

    private async Task<BridgeAuthContext?> ValidateJwtAsync(string token, CancellationToken ct)
    {
        var signingKeys = await GetSigningKeysAsync(ct);
        return ValidateTokenWithKeys(
            token,
            signingKeys,
            config.ZitadelIssuer.TrimEnd('/'),
            [config.McpResourceUri, config.ZitadelClientId],
            config.CervelloExposed);
    }

    /// <summary>
    /// Validate a JWT against the given signing keys and claim requirements.
    /// Exposed as internal so tests can inject signing keys without a live JWKS server.
    ///
    /// Parity requirements enforced here:
    ///   - ValidAlgorithms: RS256, ES256 only.
    ///   - RequireExpirationTime + ValidateLifetime: exp is always required.
    ///   - ClockSkew: zero (matches PyJWT leeway=0).
    ///   - iat claim: required (matches Python options={'require':['exp','iat','iss','aud']}).
    /// </summary>
    internal static BridgeAuthContext ValidateTokenWithKeys(
        string token,
        IEnumerable<SecurityKey> signingKeys,
        string validIssuer,
        string[] validAudiences,
        bool cervelloExposed = false)
    {
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var parameters = new TokenValidationParameters
        {
            ValidIssuers   = [validIssuer],
            ValidAudiences = validAudiences,
            IssuerSigningKeys = signingKeys,
            ValidAlgorithms = ["RS256", "ES256"],
            RequireExpirationTime = true,
            ValidateLifetime      = true,
            // Parity with PyJWT: leeway=0 (the Python default).
            // .NET default is 5 minutes; we set it to zero for strict parity.
            ClockSkew = TimeSpan.Zero,
        };

        // ValidateToken throws SecurityTokenException on rejection.
        var principal = handler.ValidateToken(token, parameters, out var validatedToken);

        // Require iat claim — Python options={'require':['exp','iat','iss','aud']}.
        // .NET enforces exp/iss/aud via ValidateLifetime/ValidIssuers/ValidAudiences;
        // iat must be checked explicitly. JwtSecurityToken.IssuedAt == DateTime.MinValue
        // when the JWT payload contains no "iat" claim.
        var jwtToken = (JwtSecurityToken)validatedToken;
        if (jwtToken.IssuedAt == DateTime.MinValue)
            throw new SecurityTokenValidationException("Token is missing the required 'iat' claim.");

        // Extract scopes from scope or scp claim (OIDC convention).
        var rawScope = principal.FindFirst("scope")?.Value
                    ?? principal.FindFirst("scp")?.Value
                    ?? "";
        var scopes = new HashSet<string>(
            rawScope.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            StringComparer.Ordinal);

        // Trusted Zitadel JWT gets the full non-sensitive legacy surface too (Python parity),
        // plus the cervello scopes when this deployment exposes cervello (CERVELLO_EXPOSED=true).
        // Zitadel's auth-code flow strips the unknown bridge:cervello:* scopes from the token
        // claim, so a cervello-exposed session would otherwise fail the scope check even though
        // CT146 (the real auth boundary) would accept the bearer.
        foreach (var s in LegacyScopes.Granted(cervelloExposed)) scopes.Add(s);

        var sub = principal.FindFirst("sub")?.Value ?? "<unknown>";

        return new BridgeAuthContext
        {
            Mode     = "jwt",
            Subject  = sub,
            Scopes   = scopes,
            RawToken = token,
        };
    }

    private async Task<IEnumerable<SecurityKey>> GetSigningKeysAsync(CancellationToken ct)
    {
        await _jwksLock.WaitAsync(ct);
        try
        {
            // Refresh only when the cache is empty or older than the refresh interval.
            if (_cachedSigningKeys is null || DateTimeOffset.UtcNow - _jwksLoadedAt > JwksRefreshInterval)
            {
                // config.JwksUrl is the RAW JWK Set endpoint ({"keys":[...]}), matching the
                // Python bridge's PyJWKClient(jwks_url) semantics. Fetch it directly and parse
                // as a JsonWebKeySet — NOT via OpenIdConnectConfigurationRetriever, which expects
                // an OIDC discovery document and returns EMPTY SigningKeys for a raw JWKS
                // (the bug this replaces: empty keys → every JWT failed IDX10500 signature check).
                //
                // GetStringAsync throws HttpRequestException on fetch failure and the
                // JsonWebKeySet ctor throws (ArgumentException/InvalidOperationException) on
                // malformed JSON. We do NOT swallow these — an empty key set must never be
                // cached, because empty keys masquerade as a signature failure (invalid_token)
                // instead of the true "JWKS unavailable" condition. The caller (InvokeAsync)
                // catches any non-SecurityTokenException and returns 401 server_error, NOT 500.
                var json = await JwksHttpClient.GetStringAsync(config.JwksUrl, ct);
                _cachedSigningKeys = new JsonWebKeySet(json).GetSigningKeys();
                _jwksLoadedAt = DateTimeOffset.UtcNow;
            }

            return _cachedSigningKeys;
        }
        finally
        {
            _jwksLock.Release();
        }
    }

    // ── Response helpers (all awaited — no fire-and-forget) ──────────────────

    private static async Task WriteMissingAuthAsync(HttpContext ctx, BridgeConfig cfg)
    {
        ctx.Response.StatusCode = 401;
        ctx.Response.Headers["WWW-Authenticate"] = WwwAuthHeader(cfg);
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync("{\"error\":\"missing_bearer\"}");
    }

    private static async Task WriteInvalidTokenAsync(HttpContext ctx, BridgeConfig cfg, string error)
    {
        ctx.Response.StatusCode = 401;
        ctx.Response.Headers["WWW-Authenticate"] = WwwAuthHeader(cfg, error);
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync($"{{\"error\":\"{error}\"}}");
    }

    private static string WwwAuthHeader(BridgeConfig cfg, string? error = null)
    {
        var resourceMd = $"{cfg.BridgeBaseUrl.TrimEnd('/')}/.well-known/oauth-protected-resource";
        var header = $"Bearer resource_metadata=\"{resourceMd}\"";
        if (error is not null) header += $", error=\"{error}\"";
        return header;
    }
}

/// <summary>
/// AsyncLocal storage for per-request auth context and source IP.
/// Tools read from here after the middleware has set the values.
/// </summary>
public static class BridgeAuthState
{
    private static readonly AsyncLocal<BridgeAuthContext?> _auth   = new();
    private static readonly AsyncLocal<string>             _ip     = new();

    public static BridgeAuthContext? CurrentAuth
    {
        get => _auth.Value;
        set => _auth.Value = value;
    }

    public static string CurrentSourceIp
    {
        get => _ip.Value ?? "unknown";
        set => _ip.Value = value;
    }
}
