using Metabase.Mcp;
using Xunit;

namespace Metabase.Mcp.Tests;

// -----------------------------------------------------------------------------
// The "no secret in an error" contract for MetabaseErrors.Sanitize: private-key
// blocks and credential assignments are redacted, the surrounding diagnostic is
// preserved, and the message is length-capped. Plain-ASCII banner -> diffs as TEXT.
// -----------------------------------------------------------------------------

/// <summary>
/// <see cref="MetabaseErrors"/> guarantees a caller-facing error message never leaks key
/// material or credentials and is length-capped. These are the fail-safe tests for the
/// "no secret in an error" contract — Metabase carries DB passwords (in <c>create_database</c>
/// connection details) and the API key header, so a pathological upstream echo must be scrubbed.
/// </summary>
public sealed class MetabaseErrorsTests
{
    [Fact]
    public void Sanitize_RedactsPrivateKeyBlock()
    {
        const string leak =
            "connect failed:\n-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA\n-----END RSA PRIVATE KEY-----\n";

        var clean = MetabaseErrors.Sanitize(leak);

        Assert.DoesNotContain("MIIEowIBAAKCAQEA", clean);
        Assert.DoesNotContain("BEGIN RSA PRIVATE KEY", clean);
        Assert.Contains("[redacted]", clean);
        Assert.Contains("connect failed", clean); // surrounding diagnostic preserved
    }

    [Fact]
    public void Sanitize_RedactsEcAndPkcs8KeyBlocks()
    {
        const string ec = "-----BEGIN EC PRIVATE KEY-----\nAAAA\n-----END EC PRIVATE KEY-----";
        const string pkcs8 = "-----BEGIN PRIVATE KEY-----\nBBBB\n-----END PRIVATE KEY-----";

        Assert.DoesNotContain("AAAA", MetabaseErrors.Sanitize(ec));
        Assert.DoesNotContain("BBBB", MetabaseErrors.Sanitize(pkcs8));
    }

    [Theory]
    [InlineData("password=hunter2", "hunter2")]
    [InlineData("db secret: s3cr3t-value", "s3cr3t-value")]
    [InlineData("Authorization: Bearer abc.def.ghi", "abc.def.ghi")]
    [InlineData("api_key=AKIAEXAMPLE", "AKIAEXAMPLE")]
    [InlineData("\"x-api-key\": \"mb-key-leaked\"", "mb-key-leaked")]
    [InlineData("token=leaked-bearer-value", "leaked-bearer-value")]
    public void Sanitize_RedactsCredentialAssignments(string input, string secret)
    {
        var clean = MetabaseErrors.Sanitize(input);

        Assert.DoesNotContain(secret, clean);
        Assert.Contains("[redacted]", clean);
    }

    [Fact]
    public void Sanitize_CapsLength()
    {
        var huge = new string('x', MetabaseErrors.MaxErrorLength + 5_000);

        var clean = MetabaseErrors.Sanitize(huge);

        Assert.True(clean.Length <= MetabaseErrors.MaxErrorLength + 32);
        Assert.EndsWith("[truncated]", clean);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sanitize_EmptyInput_GivesGenericMessage(string? input) =>
        Assert.Equal("Metabase request failed with no diagnostic output", MetabaseErrors.Sanitize(input));

    [Fact]
    public void Sanitize_PlainDiagnostic_IsPreservedVerbatim()
    {
        const string msg = "404 Not Found: no such card";
        Assert.Equal(msg, MetabaseErrors.Sanitize(msg));
    }
}
