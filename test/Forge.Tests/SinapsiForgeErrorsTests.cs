using Sinapsi.Forge.Tools;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// Pins the error-sanitization contract: every upstream/error string surfaced by a tool is
/// scrubbed of credential + private-key material and length-capped, so a token/secret that
/// reaches a forge response body or an exception can never reach a caller.
/// </summary>
public sealed class SinapsiForgeErrorsTests
{
    [Theory]
    [InlineData("token=ghp_ABC123SECRET")]
    [InlineData("api_key=sk-live-DEADBEEF")]
    [InlineData("api-key: sk-live-DEADBEEF")]
    [InlineData("secret=hunter2")]
    [InlineData("password=hunter2")]
    [InlineData("Authorization: Bearer eyJhbGciOi.SECRETPAYLOAD.sig")]
    public void Sanitize_redacts_credential_assignments(string raw)
    {
        var scrubbed = SinapsiForgeErrors.Sanitize("401 Unauthorized: " + raw);
        Assert.Contains("[redacted]", scrubbed);
        // The secret value itself must not survive.
        Assert.DoesNotContain("SECRET", scrubbed);
        Assert.DoesNotContain("hunter2", scrubbed);
        Assert.DoesNotContain("DEADBEEF", scrubbed);
        Assert.DoesNotContain("SECRETPAYLOAD", scrubbed);
    }

    [Fact]
    public void Sanitize_redacts_pem_private_key_block()
    {
        const string body =
            "500 error: -----BEGIN RSA PRIVATE KEY-----\nMIIBOgIBAAJBAKleak\n-----END RSA PRIVATE KEY-----";
        var scrubbed = SinapsiForgeErrors.Sanitize(body);
        Assert.Contains("[redacted]", scrubbed);
        Assert.DoesNotContain("PRIVATE KEY", scrubbed);
        Assert.DoesNotContain("MIIBOgIBAAJBAKleak", scrubbed);
    }

    [Fact]
    public void Sanitize_preserves_benign_diagnostics()
    {
        const string body = "404 Not Found: repository does not exist";
        Assert.Equal(body, SinapsiForgeErrors.Sanitize(body));
    }

    [Fact]
    public void Sanitize_caps_length()
    {
        var scrubbed = SinapsiForgeErrors.Sanitize(new string('z', SinapsiForgeErrors.MaxErrorLength + 500));
        Assert.True(scrubbed.Length <= SinapsiForgeErrors.MaxErrorLength + "… [truncated]".Length);
        Assert.EndsWith("… [truncated]", scrubbed);
    }

    [Fact]
    public void Sanitize_empty_yields_neutral_placeholder()
        => Assert.Equal("forge request failed with no diagnostic output", SinapsiForgeErrors.Sanitize("   "));
}
