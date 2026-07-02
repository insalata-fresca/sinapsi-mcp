// ---------------------------------------------------------------------------
// GeminiErrorsTests — the "no secret in an error" contract for GeminiErrors.
// Mirrors the StepCa.Mcp exemplar (StepCaErrorsTests): a caller-facing error
// message never leaks a private-key block or a credential assignment, and is
// length-capped. Also covers the SanitizedStderrTail helper the tools use.
// ---------------------------------------------------------------------------
using Gemini.Mcp;
using Xunit;

namespace Gemini.Mcp.Tests;

public sealed class GeminiErrorsTests
{
    [Fact]
    public void Sanitize_redacts_a_private_key_block()
    {
        const string leak =
            "auth failed:\n-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA\n-----END RSA PRIVATE KEY-----\n";

        var clean = GeminiErrors.Sanitize(leak);

        Assert.DoesNotContain("MIIEowIBAAKCAQEA", clean);
        Assert.DoesNotContain("BEGIN RSA PRIVATE KEY", clean);
        Assert.Contains("[redacted]", clean);
        Assert.Contains("auth failed", clean); // surrounding diagnostic preserved
    }

    [Fact]
    public void Sanitize_redacts_ec_and_pkcs8_key_blocks()
    {
        const string ec = "-----BEGIN EC PRIVATE KEY-----\nAAAA\n-----END EC PRIVATE KEY-----";
        const string pkcs8 = "-----BEGIN PRIVATE KEY-----\nBBBB\n-----END PRIVATE KEY-----";

        Assert.DoesNotContain("AAAA", GeminiErrors.Sanitize(ec));
        Assert.DoesNotContain("BBBB", GeminiErrors.Sanitize(pkcs8));
    }

    [Theory]
    [InlineData("password=hunter2", "hunter2")]
    [InlineData("provisioner secret: s3cr3t-value", "s3cr3t-value")]
    [InlineData("Authorization: Bearer abc.def.ghi", "abc.def.ghi")]
    [InlineData("api_key=AKIAEXAMPLE", "AKIAEXAMPLE")]
    [InlineData("NANOBANANA_API_KEY=super-secret-key", "super-secret-key")]
    public void Sanitize_redacts_credential_assignments(string input, string secret)
    {
        var clean = GeminiErrors.Sanitize(input);

        Assert.DoesNotContain(secret, clean);
        Assert.Contains("[redacted]", clean);
    }

    [Fact]
    public void Sanitize_caps_length()
    {
        var huge = new string('x', GeminiErrors.MaxErrorLength + 5_000);

        var clean = GeminiErrors.Sanitize(huge);

        Assert.True(clean.Length <= GeminiErrors.MaxErrorLength + 32);
        Assert.EndsWith("[truncated]", clean);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sanitize_empty_input_gives_a_stable_sentinel(string? input) =>
        Assert.Equal("gemini CLI failed with no diagnostic output", GeminiErrors.Sanitize(input));

    [Fact]
    public void SanitizedStderrTail_keeps_the_tail_and_scrubs_it()
    {
        // A long stderr whose secret is in the last N chars is both tailed AND scrubbed.
        var stderr = new string('.', 1_000) + "\ntoken=SECRETTOKENVALUE\n";

        var tail = GeminiErrors.SanitizedStderrTail(stderr, 100);

        Assert.True(tail.Length <= 100 + 32);
        Assert.DoesNotContain("SECRETTOKENVALUE", tail);
        Assert.Contains("[redacted]", tail);
    }

    [Fact]
    public void SanitizedStderrTail_empty_gives_the_sentinel() =>
        Assert.Equal("gemini CLI failed with no diagnostic output", GeminiErrors.SanitizedStderrTail("", 100));
}
