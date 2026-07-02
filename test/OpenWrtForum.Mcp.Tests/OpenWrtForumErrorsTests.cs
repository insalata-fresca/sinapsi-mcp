using OpenWrtForum.Mcp;
using Xunit;

namespace OpenWrtForum.Mcp.Tests;

/// <summary>
/// <see cref="OpenWrtForumErrors"/> guarantees a caller-facing error message never
/// leaks key material or credentials and is length-capped. These are the fail-safe
/// tests for the "no secret in an error" contract that every tool routes through.
/// </summary>
public sealed class OpenWrtForumErrorsTests
{
    [Fact]
    public void Sanitize_RedactsPrivateKeyBlock()
    {
        const string leak =
            "upstream error:\n-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA\n-----END RSA PRIVATE KEY-----\n";

        var clean = OpenWrtForumErrors.Sanitize(leak);

        Assert.DoesNotContain("MIIEowIBAAKCAQEA", clean);
        Assert.DoesNotContain("BEGIN RSA PRIVATE KEY", clean);
        Assert.Contains("[redacted]", clean);
        Assert.Contains("upstream error", clean); // surrounding diagnostic preserved
    }

    [Fact]
    public void Sanitize_RedactsEcAndPkcs8KeyBlocks()
    {
        const string ec = "-----BEGIN EC PRIVATE KEY-----\nAAAA\n-----END EC PRIVATE KEY-----";
        const string pkcs8 = "-----BEGIN PRIVATE KEY-----\nBBBB\n-----END PRIVATE KEY-----";

        Assert.DoesNotContain("AAAA", OpenWrtForumErrors.Sanitize(ec));
        Assert.DoesNotContain("BBBB", OpenWrtForumErrors.Sanitize(pkcs8));
    }

    [Theory]
    [InlineData("password=hunter2", "hunter2")]
    [InlineData("account secret: s3cr3t-value", "s3cr3t-value")]
    [InlineData("Authorization: Bearer abc.def.ghi", "abc.def.ghi")]
    [InlineData("api_key=AKIAEXAMPLE", "AKIAEXAMPLE")]
    [InlineData("token=eyJhbGciOi.session.cookie", "eyJhbGciOi.session.cookie")]
    public void Sanitize_RedactsCredentialAssignments(string input, string secret)
    {
        var clean = OpenWrtForumErrors.Sanitize(input);

        Assert.DoesNotContain(secret, clean);
        Assert.Contains("[redacted]", clean);
    }

    [Fact]
    public void Sanitize_CapsLength()
    {
        var huge = new string('x', OpenWrtForumErrors.MaxErrorLength + 5_000);

        var clean = OpenWrtForumErrors.Sanitize(huge);

        Assert.True(clean.Length <= OpenWrtForumErrors.MaxErrorLength + 32);
        Assert.EndsWith("[truncated]", clean);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sanitize_EmptyInput_GivesGenericMessage(string? input) =>
        Assert.Equal("forum request failed with no diagnostic output", OpenWrtForumErrors.Sanitize(input));

    [Fact]
    public void Sanitize_KeepsBenignDiagnosticText()
    {
        const string benign = "discourse 404: topic not found";
        Assert.Equal(benign, OpenWrtForumErrors.Sanitize(benign));
    }
}
