using Xunit;
using Zitadel.Mcp;

namespace Zitadel.Mcp.Tests;

/// <summary>
/// <see cref="ZitadelErrors"/> guarantees a caller-facing error message never leaks a bearer
/// token, client secret, or key material and is length-capped. These are the fail-safe tests
/// for the "no secret in an error" contract this MCP depends on — it mints PATs, OIDC client
/// secrets and machine keys.
/// </summary>
public sealed class ZitadelErrorsTests
{
    [Fact]
    public void Sanitize_RedactsPrivateKeyBlock()
    {
        const string leak =
            "upstream error:\n-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA\n-----END RSA PRIVATE KEY-----\n";

        var clean = ZitadelErrors.Sanitize(leak);

        Assert.DoesNotContain("MIIEowIBAAKCAQEA", clean);
        Assert.DoesNotContain("BEGIN RSA PRIVATE KEY", clean);
        Assert.Contains("[redacted]", clean);
        Assert.Contains("upstream error", clean); // surrounding diagnostic preserved
    }

    [Theory]
    [InlineData("password=hunter2", "hunter2")]
    [InlineData("client_secret: s3cr3t-value", "s3cr3t-value")]
    [InlineData("Authorization: Bearer abc.def.ghi", "abc.def.ghi")]
    [InlineData("api_key=AKIAEXAMPLE", "AKIAEXAMPLE")]
    [InlineData("\"clientSecret\": \"top-secret-value\"", "top-secret-value")]
    [InlineData("token=eyJhbGciOi.payload.sig", "eyJhbGciOi.payload.sig")]
    public void Sanitize_RedactsCredentialAssignments(string input, string secret)
    {
        var clean = ZitadelErrors.Sanitize(input);

        Assert.DoesNotContain(secret, clean);
        Assert.Contains("[redacted]", clean);
    }

    [Fact]
    public void Sanitize_CapsLength()
    {
        var huge = new string('x', ZitadelErrors.MaxErrorLength + 5_000);

        var clean = ZitadelErrors.Sanitize(huge);

        Assert.True(clean.Length <= ZitadelErrors.MaxErrorLength + 32);
        Assert.EndsWith("[truncated]", clean);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sanitize_EmptyInput_GivesGenericMessage(string? input) =>
        Assert.Equal("ZITADEL request failed with no diagnostic output", ZitadelErrors.Sanitize(input));

    [Fact]
    public void Sanitize_PreservesNonSensitiveDiagnostic()
    {
        const string msg = "403 Forbidden: insufficient scope for this call";
        Assert.Equal(msg, ZitadelErrors.Sanitize(msg));
    }
}
