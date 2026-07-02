using System.Reflection;
using Sinapsi.Nats;
using Xunit;

namespace Sinapsi.Nats.Tests;

// LOAD-BEARING contract: an error this library surfaces to a caller must NEVER echo
// an NKey seed, an NKey, a PEM private-key block, credentials embedded in a
// connection URL, or a password/token/bearer assignment. NatsErrors is internal, so
// we drive Sanitize/Wrap through reflection — the assert is that the secret is gone
// and [redacted] is present, plus a hard length cap.
public sealed class NatsErrorsTests
{
    private static readonly MethodInfo Sanitize =
        typeof(NatsConnectionOptions).Assembly
            .GetType("Sinapsi.Nats.NatsErrors")!
            .GetMethod("Sanitize", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo Wrap =
        typeof(NatsConnectionOptions).Assembly
            .GetType("Sinapsi.Nats.NatsErrors")!
            .GetMethod("Wrap", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static string San(string? input) => (string)Sanitize.Invoke(null, new object?[] { input })!;

    private const int MaxErrorLength = 2_000;

    [Fact]
    public void Sanitize_RedactsNKeySeed()
    {
        // A realistic-shape NATS user seed (SU + 56 base32 chars). Never a real key.
        const string seed = "SUAGMJH5XLGZKQANGWFF3VMY6D6M2HGXQ6P3ODZ2LM2AODLGYNKT2QC4NA";
        var leak = $"auth error: could not load seed {seed} for user";

        var clean = San(leak);

        Assert.DoesNotContain(seed, clean);
        Assert.Contains("[redacted]", clean);
        Assert.Contains("auth error", clean); // surrounding diagnostic preserved
    }

    [Fact]
    public void Sanitize_RedactsPublicNKey()
    {
        const string nkey = "UDXU4RCSJNZOIVERRIXP52DDVUL3M7X6ILVYMS5NKJZ7JQ6C4V4E7XZM";
        var clean = San($"nkey {nkey} rejected");
        Assert.DoesNotContain(nkey, clean);
        Assert.Contains("[redacted]", clean);
    }

    [Fact]
    public void Sanitize_RedactsPrivateKeyBlock()
    {
        const string leak =
            "tls error:\n-----BEGIN EC PRIVATE KEY-----\nMHcCAQEEIABC\n-----END EC PRIVATE KEY-----\n";

        var clean = San(leak);

        Assert.DoesNotContain("MHcCAQEEIABC", clean);
        Assert.DoesNotContain("BEGIN EC PRIVATE KEY", clean);
        Assert.Contains("[redacted]", clean);
        Assert.Contains("tls error", clean);
    }

    [Fact]
    public void Sanitize_RedactsUrlEmbeddedCredentials()
    {
        var clean = San("connect failed to nats://admin:s3cr3tpw@bus.example.com:4222");
        Assert.DoesNotContain("s3cr3tpw", clean);
        Assert.Contains("[redacted]", clean);
        // Scheme + host shape may remain; only the userinfo is stripped.
        Assert.Contains("bus.example.com", clean);
    }

    [Theory]
    [InlineData("password=hunter2", "hunter2")]
    [InlineData("provisioner secret: s3cr3t-value", "s3cr3t-value")]
    [InlineData("Authorization: Bearer abc.def.ghi", "abc.def.ghi")]
    [InlineData("api_key=AKIAEXAMPLE", "AKIAEXAMPLE")]
    [InlineData("token=eyJhbGZ.payload.sig", "eyJhbGZ.payload.sig")]
    [InlineData("seed: SUAGMJH5XLGZKQANGWFF", "SUAGMJH5XLGZKQANGWFF")]
    public void Sanitize_RedactsCredentialAssignments(string input, string secret)
    {
        var clean = San(input);
        Assert.DoesNotContain(secret, clean);
        Assert.Contains("[redacted]", clean);
    }

    [Fact]
    public void Sanitize_CapsLength()
    {
        var huge = new string('x', MaxErrorLength + 5_000);

        var clean = San(huge);

        Assert.True(clean.Length <= MaxErrorLength + 32);
        Assert.EndsWith("[truncated]", clean);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sanitize_EmptyInput_GivesGenericMessage(string? input) =>
        Assert.Equal("NATS operation failed with no diagnostic detail", San(input));

    [Fact]
    public void Wrap_SanitizesInnerMessage_AndKeepsInnerForDiagnostics()
    {
        const string seed = "SUAGMJH5XLGZKQANGWFF3VMY6D6M2HGXQ6P3ODZ2LM2AODLGYNKT2QC4NA";
        var inner = new InvalidOperationException($"boom: seed={seed}");

        var wrapped = (Exception)Wrap.Invoke(null, new object?[] { "NATS connect failed", inner })!;

        Assert.DoesNotContain(seed, wrapped.Message);
        Assert.Contains("NATS connect failed", wrapped.Message);
        Assert.Same(inner, wrapped.InnerException); // raw kept internally only
    }
}
