// Error-sanitization contract for the core MCP library. This is the load-bearing
// hardening leg: it proves that a secret embedded in the text this library would
// surface to a caller comes back [redacted] and that a pathological blob is
// length-capped. The library talks to an upstream MCP endpoint as a bearer
// identity, so any error path that quotes an upstream body must never echo a
// token, seed, signing key, or private-key block.
//
// McpSanitizer is internal; the test project sees it via InternalsVisibleTo
// wired in the library csproj. Banners are plain ASCII so this file diffs as text.
using Sinapsi.Mcp;
using Xunit;

namespace Sinapsi.Mcp.Tests;

public class McpSanitizerTests
{
    [Theory]
    [InlineData("Authorization: Bearer eyJhbGci.leaked.jwt.value")]
    [InlineData("token=super-secret-token-value")]
    [InlineData("api_key: AKIAIOSFODNN7EXAMPLE")]
    [InlineData("password=hunter2")]
    [InlineData("secret = 0123456789abcdef")]
    [InlineData("signing-key: c2lnbmluZy1rZXktbWF0ZXJpYWw=")]
    [InlineData("nkey=UBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB")]
    public void Sanitize_redacts_secret_assignments(string message)
    {
        var scrubbed = McpSanitizer.Sanitize(message);
        Assert.Contains("[redacted]", scrubbed);
        Assert.DoesNotContain("hunter2", scrubbed);
        Assert.DoesNotContain("leaked.jwt.value", scrubbed);
        Assert.DoesNotContain("super-secret-token-value", scrubbed);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", scrubbed);
    }

    [Fact]
    public void Sanitize_redacts_pem_private_key_block()
    {
        var pem =
            "call failed:\n-----BEGIN EC PRIVATE KEY-----\n" +
            "MHcCAQEEIER0d3JlYWxseXNlY3JldGtleW1hdGVyaWFsaGVyZQ==\n" +
            "-----END EC PRIVATE KEY-----\nend";
        var scrubbed = McpSanitizer.Sanitize(pem);
        Assert.Contains("[redacted]", scrubbed);
        Assert.DoesNotContain("PRIVATE KEY", scrubbed);
        Assert.DoesNotContain("MHcCAQEEIER0", scrubbed);
    }

    [Fact]
    public void Sanitize_redacts_bare_nats_seed()
    {
        // A NATS user seed (SU...) embedded in a diagnostic line.
        var seed = "SUAGC3DCICI5MSFHOK6EDHDQMQ4TZFJ7WWBS7MHDDXHUYADZKPQEXAMPLE7";
        var scrubbed = McpSanitizer.Sanitize($"connect failed with {seed}");
        Assert.Contains("[redacted]", scrubbed);
        Assert.DoesNotContain(seed, scrubbed);
    }

    [Fact]
    public void Sanitize_length_caps_a_pathological_blob()
    {
        var huge = new string('x', McpSanitizer.MaxErrorLength + 5_000);
        var scrubbed = McpSanitizer.Sanitize(huge);
        Assert.True(scrubbed.Length <= McpSanitizer.MaxErrorLength + 32);
        Assert.EndsWith("[truncated]", scrubbed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sanitize_returns_placeholder_for_blank(string? message)
    {
        Assert.Equal("(no diagnostic detail)", McpSanitizer.Sanitize(message));
    }

    [Fact]
    public void Sanitize_preserves_non_secret_diagnostics()
    {
        // A benign status line must pass through unchanged so errors stay useful.
        var scrubbed = McpSanitizer.Sanitize("upstream returned 503 service unavailable");
        Assert.Equal("upstream returned 503 service unavailable", scrubbed);
    }
}
