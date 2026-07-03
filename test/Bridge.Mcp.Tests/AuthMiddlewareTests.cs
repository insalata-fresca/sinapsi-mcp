using Bridge.Mcp.Auth;
using Xunit;

namespace Bridge.Mcp.Tests;

/// <summary>
/// Unit tests for the auth middleware helper functions.
/// These can be tested without the full ASP.NET Core pipeline.
/// </summary>
public sealed class AuthMiddlewareTests
{
    // ── Bearer extraction ─────────────────────────────────────────────────────

    [Fact]
    public void ExtractBearer_ReturnsToken_ForWellFormedHeader()
    {
        var token = BridgeAuthMiddleware.ExtractBearer("Bearer mytoken123");
        Assert.Equal("mytoken123", token);
    }

    [Fact]
    public void ExtractBearer_IsCaseInsensitiveOnBearer()
    {
        var token = BridgeAuthMiddleware.ExtractBearer("bearer MYTOKEN");
        Assert.Equal("MYTOKEN", token);
    }

    [Fact]
    public void ExtractBearer_ReturnsNull_ForMissingHeader()
    {
        Assert.Null(BridgeAuthMiddleware.ExtractBearer(null));
        Assert.Null(BridgeAuthMiddleware.ExtractBearer(""));
        Assert.Null(BridgeAuthMiddleware.ExtractBearer("   "));
    }

    [Fact]
    public void ExtractBearer_ReturnsNull_ForNonBearerScheme()
    {
        Assert.Null(BridgeAuthMiddleware.ExtractBearer("Basic dXNlcjpwYXNz"));
    }

    [Fact]
    public void ExtractBearer_TrimsToken()
    {
        var token = BridgeAuthMiddleware.ExtractBearer("Bearer   trimmed  ");
        Assert.Equal("trimmed", token);
    }

    // ── Scope constants ───────────────────────────────────────────────────────

    [Fact]
    public void LegacyScopes_ContainsExpectedScopes()
    {
        Assert.Contains("bridge:deposit",          LegacyScopes.All);
        Assert.Contains("bridge:read:documents",   LegacyScopes.All);
        Assert.Contains("bridge:read:facts",       LegacyScopes.All);
        Assert.Contains("bridge:read:emails",      LegacyScopes.All);
        Assert.Contains("bridge:context_pack",     LegacyScopes.All);
        // Sensitive scope NOT in legacy (requires Phase 5 OAuth consent).
        Assert.DoesNotContain("bridge:read:facts_sensitive", LegacyScopes.All);
    }

    // ── BridgeConfig scopes_supported ─────────────────────────────────────────

    [Fact]
    public void ScopesSupported_IncludesDeadEmailScope()
    {
        // bridge:read:emails is declared even though no tool enforces it
        // — preserve for OAuth discovery parity with the Python server.
        Assert.Contains("bridge:read:emails", BridgeConfig.ScopesSupported);
    }

    [Fact]
    public void ScopesSupported_ContainsFactsSensitive()
    {
        // bridge:read:facts_sensitive is advertised in RFC 9728 metadata.
        Assert.Contains("bridge:read:facts_sensitive", BridgeConfig.ScopesSupported);
    }
}
