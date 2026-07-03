using Bridge.Mcp.Auth;
using Xunit;

namespace Bridge.Mcp.Tests;

/// <summary>
/// Unit tests for the sliding-window rate limiter.
/// Verifies the key derivation, allow/deny logic, and bucket isolation
/// (scope matrix: deposit 30/min, read 60/min, sensitive 5/min).
/// </summary>
public sealed class RateLimiterTests
{
    // ── Key derivation ────────────────────────────────────────────────────────

    [Fact]
    public void MakeKey_IsDeterministic()
    {
        var k1 = BridgeRateLimiter.MakeKey("mytoken", "read");
        var k2 = BridgeRateLimiter.MakeKey("mytoken", "read");
        Assert.Equal(k1, k2);
    }

    [Fact]
    public void MakeKey_Differs_ByBucket()
    {
        var k1 = BridgeRateLimiter.MakeKey("tok", "read");
        var k2 = BridgeRateLimiter.MakeKey("tok", "deposit");
        Assert.NotEqual(k1, k2);
    }

    [Fact]
    public void MakeKey_Differs_ByToken()
    {
        var k1 = BridgeRateLimiter.MakeKey("tokA", "read");
        var k2 = BridgeRateLimiter.MakeKey("tokB", "read");
        Assert.NotEqual(k1, k2);
    }

    [Fact]
    public void MakeKey_PrefixIs32LowerHex()
    {
        var key = BridgeRateLimiter.MakeKey("sometoken", "bucket");
        // "sha256hex32:bucket"
        var parts = key.Split(':');
        Assert.True(parts.Length >= 2);
        Assert.Equal(32, parts[0].Length);
        Assert.All(parts[0], c => Assert.True(
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'),
            $"Expected lower hex char, got '{c}'"));
    }

    // ── Allow / deny ─────────────────────────────────────────────────────────

    [Fact]
    public void Check_AllowsUnderLimit()
    {
        var limiter = new BridgeRateLimiter();
        var result = limiter.Check("tok", "read", 60);
        Assert.True(result.Allowed);
        Assert.Equal(0.0, result.RetryAfterSeconds);
    }

    [Fact]
    public void Check_DeniesAtLimit()
    {
        var limiter = new BridgeRateLimiter();
        const string tok = "tok-rate-test";
        // Fill up the bucket (limit = 3 for test brevity).
        for (int i = 0; i < 3; i++)
            limiter.Check(tok, "test-bucket", 3);

        var result = limiter.Check(tok, "test-bucket", 3);
        Assert.False(result.Allowed);
        Assert.Equal(0, result.Remaining);
        Assert.True(result.RetryAfterSeconds > 0);
    }

    [Fact]
    public void Check_BucketsAreIsolated()
    {
        var limiter = new BridgeRateLimiter();
        const string tok = "tok-isolation";
        // Fill up "deposit" (limit 1 for test).
        limiter.Check(tok, "deposit", 1);
        var denied = limiter.Check(tok, "deposit", 1);
        Assert.False(denied.Allowed);

        // "read" bucket for the same token is independent.
        var allowed = limiter.Check(tok, "read", 1);
        Assert.True(allowed.Allowed);
    }

    [Fact]
    public void Check_TokensAreIsolated()
    {
        var limiter = new BridgeRateLimiter();
        // Fill up token A.
        limiter.Check("tokA", "read", 1);
        var denied = limiter.Check("tokA", "read", 1);
        Assert.False(denied.Allowed);

        // Token B is unaffected.
        var allowed = limiter.Check("tokB", "read", 1);
        Assert.True(allowed.Allowed);
    }

    [Fact]
    public void Check_ReturnsRemainingCount()
    {
        var limiter = new BridgeRateLimiter();
        const string tok = "tok-remaining";

        var r0 = limiter.Check(tok, "bkt", 5);
        Assert.True(r0.Allowed);
        Assert.Equal(4, r0.Remaining); // 5 - 1 = 4

        var r1 = limiter.Check(tok, "bkt", 5);
        Assert.True(r1.Allowed);
        Assert.Equal(3, r1.Remaining); // 5 - 2 = 3
    }

    // ── Scope-rate matrix (document the canonical bucket assignments) ─────────

    [Theory]
    [InlineData("deposit",   30)]
    [InlineData("read",      60)]
    [InlineData("sensitive",  5)]
    public void BucketLimits_MatchSpec(string bucket, int limit)
    {
        // Just verify the limiter works with each bucket+limit combination
        // (the actual limit values are documented, not enforced here since the
        // rate limiter is generic — the BridgeConfig enforces them).
        var limiter = new BridgeRateLimiter();
        var result = limiter.Check("tok", bucket, limit);
        Assert.True(result.Allowed);
        Assert.Equal(limit - 1, result.Remaining);
    }
}
