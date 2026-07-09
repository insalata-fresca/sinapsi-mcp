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

    [Fact]
    public void LegacyScopes_All_DoesNotContainCervelloScopes()
    {
        // The base auto-granted set must NEVER contain the cervello scopes — those are only
        // added by Granted(cervelloExposed:true). This guards a non-cervello deployment.
        Assert.DoesNotContain("bridge:cervello:read",    LegacyScopes.All);
        Assert.DoesNotContain("bridge:cervello:deposit", LegacyScopes.All);
    }

    [Fact]
    public void LegacyScopes_Granted_AddsCervelloScopes_WhenExposed()
    {
        // CERVELLO_EXPOSED=true → both cervello scopes are auto-granted (so the legacy bearer
        // and JWT paths authorize the cervello read/deposit tools), on top of the legacy set.
        var granted = LegacyScopes.Granted(cervelloExposed: true);
        Assert.Contains("bridge:cervello:read",    granted);
        Assert.Contains("bridge:cervello:deposit", granted);
        Assert.Contains("bridge:deposit",          granted); // legacy surface preserved
    }

    [Fact]
    public void LegacyScopes_Granted_OmitsCervelloScopes_WhenNotExposed()
    {
        // CERVELLO_EXPOSED=false → no cervello scopes; a non-cervello deployment never grants them.
        var granted = LegacyScopes.Granted(cervelloExposed: false);
        Assert.DoesNotContain("bridge:cervello:read",    granted);
        Assert.DoesNotContain("bridge:cervello:deposit", granted);
        Assert.Contains("bridge:deposit", granted); // legacy surface still granted
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
