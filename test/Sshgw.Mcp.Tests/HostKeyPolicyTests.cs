using System.Security.Cryptography;
using System.Text;
using Sshgw.Mcp;
using Xunit;

namespace Sshgw.Mcp.Tests;

/// <summary>
/// HostKeyPolicy is the MITM guard: SSH.NET trusts ANY host key by default, so on a
/// free read tool a spoofed host could harvest the identity key's reach or feed
/// forged output back. This suite proves the pin logic:
///   - a matching fingerprint (SHA256:base64 or hex form) ⇒ trusted;
///   - a non-matching key ⇒ rejected (fail-closed) once a pin is configured;
///   - no pin + no global requirement ⇒ trust-on-first-use;
///   - no pin + global requirement ⇒ rejected;
///   - a malformed configured fingerprint fails LOUD at construction.
/// The fingerprint is computed from the raw host-key blob deterministically, so the
/// whole policy is unit-testable without a live SSH handshake.
/// </summary>
public sealed class HostKeyPolicyTests
{
    // A deterministic synthetic host-key blob + its known fingerprints.
    private static readonly byte[] KeyBlob = Encoding.ASCII.GetBytes("synthetic-host-key-blob-v1");
    private static byte[] Digest => SHA256.HashData(KeyBlob);
    private static string Sha256B64 => "SHA256:" + Convert.ToBase64String(Digest).TrimEnd('=');
    private static string Sha256B64NoPrefix => Convert.ToBase64String(Digest).TrimEnd('=');
    private static string HexDigest => Convert.ToHexString(Digest).ToLowerInvariant();
    private static string HexDigestColoned =>
        string.Join(":", Convert.ToHexString(Digest).ToLowerInvariant().Chunk(2).Select(c => new string(c)));

    private static readonly byte[] OtherBlob = Encoding.ASCII.GetBytes("a-DIFFERENT-host-key");

    [Fact]
    public void Matching_SHA256_base64_fingerprint_is_trusted()
    {
        var policy = new HostKeyPolicy(new[] { Sha256B64 });
        Assert.True(policy.HasPin);
        Assert.True(policy.IsTrusted(KeyBlob));
        Assert.False(policy.IsTrusted(OtherBlob)); // a different key is refused
    }

    [Fact]
    public void SHA256_prefix_is_optional()
    {
        var policy = new HostKeyPolicy(new[] { Sha256B64NoPrefix });
        Assert.True(policy.IsTrusted(KeyBlob));
    }

    [Fact]
    public void Hex_fingerprint_form_is_accepted()
    {
        Assert.True(new HostKeyPolicy(new[] { HexDigest }).IsTrusted(KeyBlob));
        Assert.True(new HostKeyPolicy(new[] { HexDigestColoned }).IsTrusted(KeyBlob));
    }

    [Fact]
    public void Non_matching_key_is_rejected_when_a_pin_is_configured()
    {
        var policy = new HostKeyPolicy(new[] { Sha256B64 });
        Assert.False(policy.IsTrusted(OtherBlob));
    }

    [Fact]
    public void Multiple_pins_trust_any_of_them_supports_rotation()
    {
        var otherFp = HostKeyPolicy.FingerprintOf(OtherBlob);
        var policy = new HostKeyPolicy(new[] { Sha256B64, otherFp });
        Assert.True(policy.IsTrusted(KeyBlob));
        Assert.True(policy.IsTrusted(OtherBlob));
        Assert.False(policy.IsTrusted(Encoding.ASCII.GetBytes("yet-another-key")));
    }

    [Fact]
    public void No_pin_and_no_global_requirement_is_trust_on_first_use()
    {
        var policy = new HostKeyPolicy(fingerprints: null, requirePin: false);
        Assert.False(policy.HasPin);
        Assert.True(policy.IsTrusted(KeyBlob));   // TOFU: any key accepted
        Assert.True(policy.IsTrusted(OtherBlob));
    }

    [Fact]
    public void No_pin_but_global_requirement_rejects_every_key()
    {
        var policy = new HostKeyPolicy(fingerprints: null, requirePin: true);
        Assert.False(policy.HasPin);
        Assert.False(policy.IsTrusted(KeyBlob));   // fail-closed: no pin ⇒ no trust
        Assert.False(policy.IsTrusted(OtherBlob));
    }

    [Fact]
    public void Global_requirement_still_honours_a_configured_pin()
    {
        // requirePin=true but this server HAS a pin → the matching key is trusted,
        // a non-matching one is refused.
        var policy = new HostKeyPolicy(new[] { Sha256B64 }, requirePin: true);
        Assert.True(policy.IsTrusted(KeyBlob));
        Assert.False(policy.IsTrusted(OtherBlob));
    }

    [Fact]
    public void Empty_and_whitespace_fingerprint_entries_are_ignored()
    {
        // Blank entries must not silently become a pin (or disable pinning); a policy
        // built from only-blank entries has no pin (→ TOFU / global posture applies).
        var policy = new HostKeyPolicy(new[] { "", "   ", Sha256B64 });
        Assert.True(policy.HasPin);
        Assert.True(policy.IsTrusted(KeyBlob));

        var blankOnly = new HostKeyPolicy(new[] { "", "   " });
        Assert.False(blankOnly.HasPin);
    }

    [Theory]
    [InlineData("not-base64-or-hex-!!!")]
    [InlineData("SHA256:@@@invalid@@@")]
    [InlineData("deadbeef")]                 // hex but wrong length (not 32 bytes)
    [InlineData("AAAA")]                      // valid base64 but decodes to 3 bytes
    public void Malformed_fingerprint_throws_loud_at_construction(string bad)
    {
        // A config typo must FAIL LOUD, never silently disable the pin (which would
        // downgrade a pinned server to trust-on-first-use — the exact footgun).
        Assert.Throws<InvalidOperationException>(() => new HostKeyPolicy(new[] { bad }));
    }

    [Fact]
    public void FingerprintOf_matches_the_openssh_sha256_form()
    {
        var fp = HostKeyPolicy.FingerprintOf(KeyBlob);
        Assert.StartsWith("SHA256:", fp);
        Assert.Equal(Sha256B64, fp);
        // Round-trips: a policy built from FingerprintOf(blob) trusts that blob.
        Assert.True(new HostKeyPolicy(new[] { fp }).IsTrusted(KeyBlob));
    }
}
