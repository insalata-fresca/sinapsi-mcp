using Xunit;

namespace Gdrive.Mcp.Tests;

/// <summary>
/// <see cref="GdriveErrors"/> guarantees a caller-facing error message never
/// leaks an OAuth/refresh token, a client secret, or key material, and is
/// length-capped. These are the fail-safe tests for the "no secret in an error"
/// contract that every Drive tool's error path relies on.
/// </summary>
public sealed class GdriveErrorsTests
{
    [Fact]
    public void Sanitize_RedactsPrivateKeyBlock()
    {
        const string leak =
            "auth failed:\n-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA\n-----END RSA PRIVATE KEY-----\n";

        var clean = GdriveErrors.Sanitize(leak);

        Assert.DoesNotContain("MIIEowIBAAKCAQEA", clean);
        Assert.DoesNotContain("BEGIN RSA PRIVATE KEY", clean);
        Assert.Contains("[redacted]", clean);
        Assert.Contains("auth failed", clean); // surrounding diagnostic preserved
    }

    [Theory]
    [InlineData("password=hunter2", "hunter2")]
    [InlineData("client secret: s3cr3t-value", "s3cr3t-value")]
    [InlineData("Authorization: Bearer abc.def.ghi", "abc.def.ghi")]
    [InlineData("api_key=AKIAEXAMPLE", "AKIAEXAMPLE")]
    [InlineData("refresh_token=1//0gVeryLongOAuthRefreshToken", "1//0gVeryLongOAuthRefreshToken")]
    [InlineData("access_token: ya29.a0AfExampleAccessToken", "ya29.a0AfExampleAccessToken")]
    [InlineData("client_secret=GOCSPX-exampleClientSecret", "GOCSPX-exampleClientSecret")]
    public void Sanitize_RedactsCredentialAssignments(string input, string secret)
    {
        var clean = GdriveErrors.Sanitize(input);

        Assert.DoesNotContain(secret, clean);
        Assert.Contains("[redacted]", clean);
    }

    [Fact]
    public void Sanitize_CapsLength()
    {
        var huge = new string('x', GdriveErrors.MaxErrorLength + 5_000);

        var clean = GdriveErrors.Sanitize(huge);

        Assert.True(clean.Length <= GdriveErrors.MaxErrorLength + 32);
        Assert.EndsWith("[truncated]", clean);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sanitize_EmptyInput_GivesGenericMessage(string? input) =>
        Assert.Equal("Drive request failed with no diagnostic output", GdriveErrors.Sanitize(input));

    [Fact]
    public void Sanitize_KeepsNonSecretDiagnostic()
    {
        const string msg = "The user does not have sufficient permissions for file 1a2b3c.";
        Assert.Equal(msg, GdriveErrors.Sanitize(msg));
    }

    [Fact]
    public void FromException_ScrubsMessage()
    {
        // Diagnostic BEFORE the secret is preserved; the credential (redacted to
        // end-of-line by the sanitiser) is gone.
        var ex = new InvalidOperationException("Google rejected the request: token=SECRETVALUE");
        var msg = GdriveErrors.FromException(ex);

        Assert.DoesNotContain("SECRETVALUE", msg);
        Assert.Contains("[redacted]", msg);
        Assert.Contains("Google rejected the request", msg);
    }
}
