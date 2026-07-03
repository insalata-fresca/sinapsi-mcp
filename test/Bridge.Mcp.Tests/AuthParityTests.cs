using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Bridge.Mcp.Auth;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Bridge.Mcp.Tests;

/// <summary>
/// Auth parity tests — verifies that the C# middleware enforces the same claim
/// requirements as the Python bridge (auth.py options={'require':['exp','iat','iss','aud']}).
///
/// Uses a locally-generated RSA key pair so tests run offline (no live JWKS server).
/// The <see cref="BridgeAuthMiddleware.ValidateTokenWithKeys"/> internal surface
/// accepts pre-loaded signing keys, which lets us inject keys and test corner cases.
/// </summary>
public sealed class AuthParityTests
{
    // ── RSA key pair reused across tests in this class ────────────────────────

    private static readonly RSA _rsa = RSA.Create(2048);
    private static readonly RsaSecurityKey _privateKey = new(_rsa);
    private static readonly RsaSecurityKey _publicKey  = new(_rsa);

    private const string Issuer   = "https://auth.example.com";
    private const string Audience = "https://mcp.example.com";

    // ── Token factory ─────────────────────────────────────────────────────────

    /// <summary>
    /// Mint a signed RS256 JWT with caller-specified claims.
    /// includeIat=false emits no "iat" claim (used to test iat-required enforcement).
    /// </summary>
    private static string MintToken(
        bool includeIat = true,
        bool includeExp = true,
        bool includeAud = true,
        string issuer = Issuer,
        string? subject = "user-123",
        int expOffsetSeconds = 3600)
    {
        var handler = new JwtSecurityTokenHandler { SetDefaultTimesOnTokenCreation = false };
        var now = DateTime.UtcNow;

        var claims = new List<Claim>();
        if (subject is not null) claims.Add(new Claim("sub", subject));

        var descriptor = new SecurityTokenDescriptor
        {
            Subject            = new ClaimsIdentity(claims),
            Issuer             = issuer,
            Audience           = includeAud ? Audience : null,
            IssuedAt           = includeIat ? now : null,
            Expires            = includeExp ? now.AddSeconds(expOffsetSeconds) : null,
            SigningCredentials = new SigningCredentials(_privateKey, SecurityAlgorithms.RsaSha256),
        };

        var token = handler.CreateJwtSecurityToken(descriptor);
        return handler.WriteToken(token);
    }

    // ── iat claim required (parity with Python 'require':['iat']) ─────────────

    [Fact]
    public void ValidateTokenWithKeys_Accepts_TokenWithIat()
    {
        var token = MintToken(includeIat: true);
        // Should not throw — all required claims present.
        var ctx = BridgeAuthMiddleware.ValidateTokenWithKeys(
            token, [_publicKey], Issuer, [Audience]);
        Assert.Equal("jwt", ctx.Mode);
        Assert.Equal("user-123", ctx.Subject);
    }

    [Fact]
    public void ValidateTokenWithKeys_Rejects_TokenMissingIat()
    {
        // A validly-signed token WITHOUT an "iat" claim should be rejected
        // (Python: options={'require':['exp','iat','iss','aud']}).
        var token = MintToken(includeIat: false);
        var ex = Assert.Throws<SecurityTokenValidationException>(() =>
            BridgeAuthMiddleware.ValidateTokenWithKeys(
                token, [_publicKey], Issuer, [Audience]));
        Assert.Contains("iat", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── ClockSkew = zero (parity with PyJWT leeway=0) ─────────────────────────

    [Fact]
    public void ValidateTokenWithKeys_Rejects_JustExpiredToken()
    {
        // Token expired 1 second ago. With ClockSkew=0 this MUST be rejected.
        // With the old .NET default ClockSkew of 5 min it would be accepted —
        // this test guards the parity fix.
        var token = MintToken(expOffsetSeconds: -1);
        var ex = Assert.ThrowsAny<SecurityTokenException>(() =>
            BridgeAuthMiddleware.ValidateTokenWithKeys(
                token, [_publicKey], Issuer, [Audience]));
        // SecurityTokenExpiredException is a subclass of SecurityTokenException.
        Assert.True(
            ex is SecurityTokenExpiredException,
            $"Expected SecurityTokenExpiredException, got: {ex.GetType().Name}: {ex.Message}");
    }

    [Fact]
    public void ValidateTokenWithKeys_Accepts_JustFreshToken()
    {
        // Token that expires in 30 seconds — must be accepted (exp in the future).
        var token = MintToken(expOffsetSeconds: 30);
        var ctx = BridgeAuthMiddleware.ValidateTokenWithKeys(
            token, [_publicKey], Issuer, [Audience]);
        Assert.Equal("jwt", ctx.Mode);
    }

    // ── exp claim required ────────────────────────────────────────────────────

    [Fact]
    public void ValidateTokenWithKeys_Rejects_TokenMissingExp()
    {
        // RequireExpirationTime=true; a token without exp must be rejected.
        var token = MintToken(includeExp: false);
        Assert.ThrowsAny<SecurityTokenException>(() =>
            BridgeAuthMiddleware.ValidateTokenWithKeys(
                token, [_publicKey], Issuer, [Audience]));
    }

    // ── aud claim required ────────────────────────────────────────────────────

    [Fact]
    public void ValidateTokenWithKeys_Rejects_TokenMissingAud()
    {
        var token = MintToken(includeAud: false);
        Assert.ThrowsAny<SecurityTokenException>(() =>
            BridgeAuthMiddleware.ValidateTokenWithKeys(
                token, [_publicKey], Issuer, [Audience]));
    }

    // ── issuer mismatch ───────────────────────────────────────────────────────

    [Fact]
    public void ValidateTokenWithKeys_Rejects_WrongIssuer()
    {
        var token = MintToken(issuer: "https://evil.example.com");
        Assert.ThrowsAny<SecurityTokenException>(() =>
            BridgeAuthMiddleware.ValidateTokenWithKeys(
                token, [_publicKey], Issuer, [Audience]));
    }

    // ── wrong key ─────────────────────────────────────────────────────────────

    [Fact]
    public void ValidateTokenWithKeys_Rejects_WrongSigningKey()
    {
        var token = MintToken();
        using var wrongRsa = RSA.Create(2048);
        var wrongKey = new RsaSecurityKey(wrongRsa);
        Assert.ThrowsAny<SecurityTokenException>(() =>
            BridgeAuthMiddleware.ValidateTokenWithKeys(
                token, [wrongKey], Issuer, [Audience]));
    }

    // ── scope extraction ──────────────────────────────────────────────────────

    [Fact]
    public void ValidateTokenWithKeys_ExtractsScope_AndMergesLegacy()
    {
        var handler = new JwtSecurityTokenHandler { SetDefaultTimesOnTokenCreation = false };
        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([
                new Claim("sub",   "user-scope"),
                new Claim("scope", "bridge:read:facts_sensitive custom:scope"),
            ]),
            Issuer             = Issuer,
            Audience           = Audience,
            IssuedAt           = now,
            Expires            = now.AddHours(1),
            SigningCredentials = new SigningCredentials(_privateKey, SecurityAlgorithms.RsaSha256),
        };
        var token = handler.WriteToken(handler.CreateJwtSecurityToken(descriptor));

        var ctx = BridgeAuthMiddleware.ValidateTokenWithKeys(
            token, [_publicKey], Issuer, [Audience]);

        // JWT scopes + legacy scopes must all be present.
        Assert.Contains("bridge:read:facts_sensitive", ctx.Scopes);
        Assert.Contains("custom:scope",                ctx.Scopes);
        Assert.Contains("bridge:deposit",              ctx.Scopes);  // from LegacyScopes
        Assert.Equal("user-scope", ctx.Subject);
    }

    // ── JWKS fetch failure → SecurityTokenException path ─────────────────────
    //
    // We cannot unit-test the middleware's HTTP-exception→401 path without a full
    // ASP.NET TestServer, but we can verify that the exception flows as expected by
    // checking that GetSigningKeysAsync throws a non-SecurityTokenException type
    // when the JWKS URL is unreachable. This guards that our broader catch block in
    // InvokeAsync is necessary and fires on the right exception type.

    [Fact]
    public async Task GetSigningKeys_ThrowsNonSecurityTokenException_OnBadUrl()
    {
        // Construct a middleware instance pointing at a URL that will fail to connect.
        // JwksUrl is derived as ZitadelIssuer + "/oauth/v2/keys"; set a loopback port
        // that is guaranteed to be unreachable to trigger an HttpRequestException.
        var cfg = new BridgeConfig
        {
            ZitadelIssuer  = "http://localhost:19999",   // JwksUrl → ...19999/oauth/v2/keys
            McpResourceUri = Audience,
        };

        // We test via reflection since GetSigningKeysAsync is private.
        // The important invariant: the exception is NOT a SecurityTokenException subclass
        // (which means it would escape the original `catch (SecurityTokenException)` block
        // and become an unhandled 500 without the broader catch we added).
        var middleware = new BridgeAuthMiddleware(
            _ => Task.CompletedTask,
            cfg,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BridgeAuthMiddleware>.Instance);

        var method = typeof(BridgeAuthMiddleware)
            .GetMethod("GetSigningKeysAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        var task = (Task<System.Collections.Generic.IEnumerable<SecurityKey>>)
            method.Invoke(middleware, [CancellationToken.None])!;

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => task);

        // The exception MUST NOT be a SecurityTokenException subtype
        // (those are caught by the JWT-rejection path; JWKS failures must go
        // through the separate "server_error" catch we added in InvokeAsync).
        Assert.False(
            ex is SecurityTokenException,
            $"Expected a non-SecurityTokenException (e.g. HttpRequestException/InvalidOperationException) " +
            $"for JWKS fetch failure, got: {ex.GetType().FullName}");
    }
}
