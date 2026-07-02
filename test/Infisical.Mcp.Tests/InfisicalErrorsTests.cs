using Xunit;

namespace Infisical.Mcp.Tests;

/// <summary>
/// <see cref="InfisicalErrors"/> guarantees a caller-facing error message never leaks a
/// secret value, a credential, or key material, and is length-capped. These are the
/// fail-safe tests for the "no secret in an error" contract — load-bearing for a server
/// whose whole purpose is transcript-safety.
/// </summary>
public sealed class InfisicalErrorsTests
{
    [Fact]
    public void Sanitize_RedactsPrivateKeyBlock()
    {
        const string leak =
            "store failed:\n-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA\n-----END RSA PRIVATE KEY-----\n";

        var clean = InfisicalErrors.Sanitize(leak);

        Assert.DoesNotContain("MIIEowIBAAKCAQEA", clean);
        Assert.DoesNotContain("BEGIN RSA PRIVATE KEY", clean);
        Assert.Contains("[redacted]", clean);
        Assert.Contains("store failed", clean); // surrounding diagnostic preserved
    }

    [Fact]
    public void Sanitize_RedactsEcAndPkcs8KeyBlocks()
    {
        const string ec = "-----BEGIN EC PRIVATE KEY-----\nAAAA\n-----END EC PRIVATE KEY-----";
        const string pkcs8 = "-----BEGIN PRIVATE KEY-----\nBBBB\n-----END PRIVATE KEY-----";

        Assert.DoesNotContain("AAAA", InfisicalErrors.Sanitize(ec));
        Assert.DoesNotContain("BBBB", InfisicalErrors.Sanitize(pkcs8));
    }

    [Theory]
    [InlineData("password=hunter2", "hunter2")]
    [InlineData("provisioner secret: s3cr3t-value", "s3cr3t-value")]
    [InlineData("Authorization: Bearer abc.def.ghi", "abc.def.ghi")]
    [InlineData("api_key=AKIAEXAMPLE", "AKIAEXAMPLE")]
    [InlineData("accessToken=eyJhbGci.payload.sig", "eyJhbGci.payload.sig")]
    [InlineData("\"secretValue\":\"topsecret\"", "topsecret")]
    public void Sanitize_RedactsCredentialAssignments(string input, string secret)
    {
        var clean = InfisicalErrors.Sanitize(input);

        Assert.DoesNotContain(secret, clean);
        Assert.Contains("[redacted]", clean);
    }

    [Fact]
    public void Sanitize_CapsLength()
    {
        var huge = new string('x', InfisicalErrors.MaxErrorLength + 5_000);

        var clean = InfisicalErrors.Sanitize(huge);

        Assert.True(clean.Length <= InfisicalErrors.MaxErrorLength + 32);
        Assert.EndsWith("[truncated]", clean);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sanitize_EmptyInput_GivesGenericMessage(string? input) =>
        Assert.Equal("infisical request failed with no diagnostic output", InfisicalErrors.Sanitize(input));

    [Fact]
    public void Sanitize_PreservesNonSecretDiagnostics()
    {
        const string msg = "set secret /web/api/K: POST 500, PATCH 403";
        Assert.Equal(msg, InfisicalErrors.Sanitize(msg));
    }
}
