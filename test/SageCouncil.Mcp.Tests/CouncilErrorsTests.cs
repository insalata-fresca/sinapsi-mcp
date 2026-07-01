using Xunit;

namespace SageCouncil.Mcp.Tests;

// -----------------------------------------------------------------------------
// Error-scrub contract for CouncilErrors.Sanitize: no secret leaks (PEM private
// keys + password/secret/token/api-key/bearer/authorization assignments are
// redacted), and the message is length-capped.
// -----------------------------------------------------------------------------

public sealed class CouncilErrorsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_input_yields_the_stable_no_output_sentinel(string? input)
    {
        Assert.Equal("council member failed with no diagnostic output", CouncilErrors.Sanitize(input));
    }

    [Fact]
    public void A_pem_private_key_block_is_redacted_whole()
    {
        var msg =
            "backend error before key\n" +
            "-----BEGIN PRIVATE KEY-----\nMIIsecretkeymaterialdonotleak\n-----END PRIVATE KEY-----\n" +
            "and after";
        var scrubbed = CouncilErrors.Sanitize(msg);

        Assert.DoesNotContain("MIIsecretkeymaterialdonotleak", scrubbed);
        Assert.Contains("[redacted]", scrubbed);
        // Non-secret diagnostic context survives.
        Assert.Contains("backend error before key", scrubbed);
        Assert.Contains("and after", scrubbed);
    }

    [Theory]
    [InlineData("token=hunter2-supersecret", "hunter2-supersecret")]
    [InlineData("password: p@ssw0rd-do-not-leak", "p@ssw0rd-do-not-leak")]
    [InlineData("api_key = AKIAEXAMPLE-do-not-leak", "AKIAEXAMPLE-do-not-leak")]
    [InlineData("Authorization: Bearer eyJhbGc.doNotLeakThisJwt.sig", "doNotLeakThisJwt")]
    public void A_credential_assignment_has_its_value_redacted(string input, string secret)
    {
        var scrubbed = CouncilErrors.Sanitize($"upstream said: {input}");

        Assert.DoesNotContain(secret, scrubbed);
        Assert.Contains("[redacted]", scrubbed);
        // The key name is preserved for diagnosability.
        Assert.Contains("upstream said", scrubbed);
    }

    [Fact]
    public void An_oversize_message_is_length_capped()
    {
        var huge = new string('z', CouncilErrors.MaxErrorLength + 5_000);
        var scrubbed = CouncilErrors.Sanitize(huge);

        Assert.True(scrubbed.Length <= CouncilErrors.MaxErrorLength + 32);
        Assert.EndsWith("[truncated]", scrubbed);
    }

    [Fact]
    public void A_clean_diagnostic_message_passes_through_unchanged()
    {
        // The plain error strings the council tests assert on must survive intact.
        const string msg = "member deadline exceeded (150ms)";
        Assert.Equal(msg, CouncilErrors.Sanitize(msg));
    }
}
