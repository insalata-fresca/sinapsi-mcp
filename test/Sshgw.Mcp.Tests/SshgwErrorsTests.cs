using Sshgw.Mcp;
using Xunit;

namespace Sshgw.Mcp.Tests;

/// <summary>
/// <see cref="SshgwErrors.Sanitize"/> guarantees a caller-facing string (a remote
/// command's surfaced stderr) never leaks key material or credentials, and is
/// length-capped. These are the fail-safe tests for the "no secret in a surfaced
/// error" contract. Empty/whitespace input returns null (an absent field), so a
/// clean command keeps a null stderr rather than a placeholder.
/// </summary>
public sealed class SshgwErrorsTests
{
    [Fact]
    public void Sanitize_RedactsPrivateKeyBlock()
    {
        const string leak =
            "boom:\n-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA\n-----END RSA PRIVATE KEY-----\n";

        var clean = SshgwErrors.Sanitize(leak)!;

        Assert.DoesNotContain("MIIEowIBAAKCAQEA", clean);
        Assert.DoesNotContain("BEGIN RSA PRIVATE KEY", clean);
        Assert.Contains("[redacted]", clean);
        Assert.Contains("boom", clean); // surrounding diagnostic preserved
    }

    [Fact]
    public void Sanitize_RedactsEcAndPkcs8AndOpenSshKeyBlocks()
    {
        const string ec = "-----BEGIN EC PRIVATE KEY-----\nAAAA\n-----END EC PRIVATE KEY-----";
        const string pkcs8 = "-----BEGIN PRIVATE KEY-----\nBBBB\n-----END PRIVATE KEY-----";
        const string openssh = "-----BEGIN OPENSSH PRIVATE KEY-----\nCCCC\n-----END OPENSSH PRIVATE KEY-----";

        Assert.DoesNotContain("AAAA", SshgwErrors.Sanitize(ec)!);
        Assert.DoesNotContain("BBBB", SshgwErrors.Sanitize(pkcs8)!);
        Assert.DoesNotContain("CCCC", SshgwErrors.Sanitize(openssh)!);
    }

    [Theory]
    [InlineData("password=hunter2", "hunter2")]
    [InlineData("provisioner secret: s3cr3t-value", "s3cr3t-value")]
    [InlineData("Authorization: Bearer abc.def.ghi", "abc.def.ghi")]
    [InlineData("api_key=AKIAEXAMPLE", "AKIAEXAMPLE")]
    [InlineData("token=eyJm_fake.jwt", "eyJm_fake.jwt")]
    public void Sanitize_RedactsCredentialAssignments(string input, string secret)
    {
        var clean = SshgwErrors.Sanitize(input)!;

        Assert.DoesNotContain(secret, clean);
        Assert.Contains("[redacted]", clean);
    }

    [Fact]
    public void Sanitize_CapsLength()
    {
        var huge = new string('x', SshgwErrors.MaxErrorLength + 5_000);

        var clean = SshgwErrors.Sanitize(huge)!;

        Assert.True(clean.Length <= SshgwErrors.MaxErrorLength + 32);
        Assert.EndsWith("[truncated]", clean);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sanitize_EmptyInput_ReturnsNull(string? input) =>
        Assert.Null(SshgwErrors.Sanitize(input));

    [Fact]
    public void Sanitize_PlainDiagnostic_IsPreservedTrimmed()
    {
        // A benign stderr with no secret is returned intact (trimmed), never
        // mangled — only credential-shaped substrings are touched.
        Assert.Equal("no such file or directory", SshgwErrors.Sanitize("  no such file or directory  "));
    }
}
