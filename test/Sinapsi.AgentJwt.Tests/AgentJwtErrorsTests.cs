using Sinapsi.AgentJwt;
using Xunit;

namespace Sinapsi.AgentJwt.Tests;

// Plain-ASCII comment banner so this file diffs as TEXT.
//
// The LOAD-BEARING leg: prove that a secret embedded in any string the library
// would surface to a caller is [redacted] before it leaves the process, and
// that a pathological blob is length-capped. A mint failure must NEVER echo the
// RSA signing key, an NKey/seed, or a bearer/authorization/token/secret value.

public sealed class AgentJwtErrorsTests
{
    private const string Redacted = "[redacted]";

    [Fact]
    public void Sanitize_Null_ReturnsNeutralMessage()
    {
        Assert.Equal("JWT mint failed with no diagnostic detail", AgentJwtErrors.Sanitize(null));
    }

    [Fact]
    public void Sanitize_Whitespace_ReturnsNeutralMessage()
    {
        Assert.Equal("JWT mint failed with no diagnostic detail", AgentJwtErrors.Sanitize("   \n\t "));
    }

    [Fact]
    public void Sanitize_RedactsRsaPrivateKeyBlock()
    {
        const string key =
            "-----BEGIN RSA PRIVATE KEY-----\n" +
            "MIIEpAIBAAKCAQEA0Z1v3q7WvVeryZecretKeyMaterialHere\n" +
            "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789+/AbCdEfGh==\n" +
            "-----END RSA PRIVATE KEY-----";
        var msg = $"import failed for key: {key}";

        var scrubbed = AgentJwtErrors.Sanitize(msg);

        Assert.Contains(Redacted, scrubbed);
        Assert.DoesNotContain("MIIEpAIBAAKCAQEA", scrubbed);
        Assert.DoesNotContain("SecretKeyMaterialHere", scrubbed);
        Assert.DoesNotContain("PRIVATE KEY", scrubbed);
    }

    [Fact]
    public void Sanitize_RedactsPkcs8PrivateKeyBlock()
    {
        const string key =
            "-----BEGIN PRIVATE KEY-----\n" +
            "MIIEvQIBADANBgkqhkiG9w0BAQEFAASCleaked8pkcs8material\n" +
            "-----END PRIVATE KEY-----";
        var scrubbed = AgentJwtErrors.Sanitize($"boom {key} tail");

        Assert.DoesNotContain("MIIEvQIBADANBgkqhkiG9w0", scrubbed);
        Assert.DoesNotContain("leaked8pkcs8material", scrubbed);
        Assert.Contains(Redacted, scrubbed);
    }

    [Fact]
    public void Sanitize_RedactsNKeySeed()
    {
        // A NATS NKey user seed: 'S' + 'U' + base32. Synthetic, not a real seed.
        const string seed = "SUAGC3DELVYAOOAOUY2QQ3E4LDMRQPTLYNZPXK5N3XY2JW7ADXZ3T4YZ7Q";
        var scrubbed = AgentJwtErrors.Sanitize($"nats connect failed with seed {seed} oops");

        Assert.DoesNotContain(seed, scrubbed);
        Assert.Contains(Redacted, scrubbed);
    }

    [Theory]
    [InlineData("password=hunter2 rest")]
    [InlineData("secret: s3cr3t-value trailing")]
    [InlineData("token=eyJabc.def.ghi more")]
    [InlineData("api_key=AKIA_something extra")]
    [InlineData("api-key: AKIA_something extra")]
    [InlineData("Authorization: Bearer eyJ0eXA.payload.sig")]
    [InlineData("bearer=eyJ0eXA.payload.sig")]
    [InlineData("nkey=SUAGC3DELVYAOOAOUY2 more")]
    [InlineData("signing_key=abc123def456 tail")]
    public void Sanitize_RedactsSecretAssignments(string raw)
    {
        var scrubbed = AgentJwtErrors.Sanitize(raw);

        Assert.Contains(Redacted, scrubbed);
        // The sensitive VALUE tokens must all be gone.
        Assert.DoesNotContain("hunter2", scrubbed);
        Assert.DoesNotContain("s3cr3t-value", scrubbed);
        Assert.DoesNotContain("eyJabc.def.ghi", scrubbed);
        Assert.DoesNotContain("AKIA_something", scrubbed);
        Assert.DoesNotContain("eyJ0eXA.payload.sig", scrubbed);
        Assert.DoesNotContain("abc123def456", scrubbed);
    }

    [Fact]
    public void Sanitize_PreservesKeyNameForDiagnosability()
    {
        // The key NAME is kept (so an operator can tell WHAT leaked), only the
        // value is redacted.
        var scrubbed = AgentJwtErrors.Sanitize("password=hunter2");
        Assert.Contains("password", scrubbed);
        Assert.DoesNotContain("hunter2", scrubbed);
    }

    [Fact]
    public void Sanitize_LengthCapsPathologicalInput()
    {
        var huge = new string('x', AgentJwtErrors.MaxErrorLength + 5_000);
        var scrubbed = AgentJwtErrors.Sanitize(huge);

        Assert.True(scrubbed.Length <= AgentJwtErrors.MaxErrorLength + 32,
            $"expected length-capped output, got {scrubbed.Length}");
        Assert.EndsWith("[truncated]", scrubbed);
    }

    [Fact]
    public void Sanitize_LeavesBenignTextUntouched()
    {
        const string benign = "OIDC token HTTP 401: invalid_grant";
        Assert.Equal(benign, AgentJwtErrors.Sanitize(benign));
    }
}
