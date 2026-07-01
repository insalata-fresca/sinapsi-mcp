// ---------------------------------------------------------------------------
// IndexerErrorsTests - the "no secret in an error" + length-cap contract for
// IndexerErrors.Sanitize / FromException.
// Plain-ASCII banner so this source diffs as TEXT, never binary.
// ---------------------------------------------------------------------------

using Sinapsi.Indexer;
using Xunit;

namespace Sinapsi.Indexer.Tests;

/// <summary>
/// <see cref="IndexerErrors"/> guarantees a caller-facing error never leaks a DB
/// password, a forge/NATS token, a bearer value, or PEM key material, and is
/// length-capped. These are the fail-safe tests for that contract.
/// </summary>
public sealed class IndexerErrorsTests
{
    [Theory]
    [InlineData("connection failed: password=hunter2 while connecting", "hunter2")]
    [InlineData("git remote: token: ghp_ABCDEF123456", "ghp_ABCDEF123456")]
    [InlineData("Authorization: Bearer abc.def.ghi", "abc.def.ghi")]
    [InlineData("api_key=AKIAEXAMPLE denied", "AKIAEXAMPLE")]
    [InlineData("secret = s3cr3t-value", "s3cr3t-value")]
    public void Sanitize_RedactsCredentialAssignments(string input, string secret)
    {
        var clean = IndexerErrors.Sanitize(input);
        Assert.DoesNotContain(secret, clean);
        Assert.Contains("[redacted]", clean);
    }

    [Fact]
    public void Sanitize_RedactsPrivateKeyBlock()
    {
        const string leak =
            "error:\n-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA\n-----END RSA PRIVATE KEY-----\n";
        var clean = IndexerErrors.Sanitize(leak);
        Assert.DoesNotContain("MIIEowIBAAKCAQEA", clean);
        Assert.Contains("[redacted]", clean);
        Assert.Contains("error", clean); // surrounding diagnostic preserved
    }

    [Fact]
    public void Sanitize_PreservesNonSecretDiagnostics()
    {
        const string msg = "relation \"documents\" does not exist";
        Assert.Equal(msg, IndexerErrors.Sanitize(msg));
    }

    [Fact]
    public void Sanitize_CapsLength()
    {
        var huge = new string('x', IndexerErrors.MaxErrorLength + 5_000);
        var clean = IndexerErrors.Sanitize(huge);
        Assert.True(clean.Length <= IndexerErrors.MaxErrorLength + 32);
        Assert.EndsWith("[truncated]", clean);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sanitize_EmptyInput_GivesGenericMessage(string? input) =>
        Assert.Equal("indexer operation failed with no diagnostic output", IndexerErrors.Sanitize(input));

    [Fact]
    public void FromException_PrefixesTypeAndScrubsMessage()
    {
        var ex = new InvalidOperationException("db down: password=leaky here");
        var msg = IndexerErrors.FromException(ex);
        Assert.Contains(nameof(InvalidOperationException), msg);
        Assert.DoesNotContain("leaky", msg);
        Assert.Contains("[redacted]", msg);
    }
}
