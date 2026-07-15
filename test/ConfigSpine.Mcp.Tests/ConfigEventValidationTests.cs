using Xunit;

namespace ConfigSpine.Mcp.Tests;

/// <summary>
/// The least-privilege guard, unit-tested directly: no caller-supplied value may compose a subject
/// outside <c>homelab.config.&gt;</c>. Covers ctid/token validation, the composed subject, and the
/// defence-in-depth subtree re-check.
/// </summary>
public sealed class ConfigEventValidationTests
{
    // --- ctid ---

    [Theory]
    [InlineData("105")]
    [InlineData("1")]
    [InlineData("146")]
    public void ValidateCtid_accepts_numeric(string ctid) =>
        Assert.Null(ConfigEventValidation.ValidateCtid(ctid));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("105a")]        // non-digit
    [InlineData("10.5")]        // extra token via dot
    [InlineData("105>")]        // wildcard
    [InlineData("*")]           // wildcard
    [InlineData("1234567")]     // over-long (max 6)
    public void ValidateCtid_rejects_non_numeric_or_unbounded(string? ctid) =>
        Assert.NotNull(ConfigEventValidation.ValidateCtid(ctid));

    // --- entity / action tokens ---

    [Theory]
    [InlineData("acl")]
    [InlineData("cert")]
    [InlineData("env_file")]
    [InlineData("route-table")]
    public void ValidateToken_accepts_single_slug(string value) =>
        Assert.Null(ConfigEventValidation.ValidateToken(value, "entity"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a.b")]         // extra token
    [InlineData("*")]           // wildcard token
    [InlineData(">")]           // full wildcard
    [InlineData("a>b")]         // embedded wildcard
    [InlineData("a b")]         // whitespace
    [InlineData("a/b")]         // separator
    [InlineData("a\\b")]        // separator
    [InlineData("-lead")]       // leading dash
    public void ValidateToken_rejects_anything_that_could_escape_the_token(string? value) =>
        Assert.NotNull(ConfigEventValidation.ValidateToken(value, "entity"));

    [Fact]
    public void ValidateToken_rejects_control_char()
    {
        Assert.NotNull(ConfigEventValidation.ValidateToken("a\nb", "action"));
        Assert.NotNull(ConfigEventValidation.ValidateToken("a\0b", "action"));
    }

    // --- composed subject + subtree guard ---

    [Fact]
    public void BuildSubject_composes_the_rule6_shape() =>
        Assert.Equal("homelab.config.105.acl.added",
            ConfigEventValidation.BuildSubject("105", "acl", "added"));

    [Fact]
    public void EnsureInConfigSubtree_accepts_a_well_formed_config_subject() =>
        Assert.Null(ConfigEventValidation.EnsureInConfigSubtree("homelab.config.105.acl.added"));

    [Theory]
    [InlineData("homelab.security.authz.q1.cse")]   // different subtree
    [InlineData("homelab.config.105.acl")]          // too few tokens
    [InlineData("homelab.config.105.acl.added.x")]  // too many tokens
    [InlineData("homelab.config.105..added")]       // empty token
    [InlineData("homelab.deploy.105.svc.applied")]  // sibling subtree
    [InlineData("config.105.acl.added")]            // missing root
    public void EnsureInConfigSubtree_rejects_anything_outside_homelab_config(string subject) =>
        Assert.NotNull(ConfigEventValidation.EnsureInConfigSubtree(subject));

    // --- error sanitizer ---

    [Fact]
    public void Sanitize_redacts_a_seed_like_and_url_like_substring()
    {
        const string seed = "SUAABCDEFGHIJKLMNOPQRSTUVWXYZ234567ABCDEFGHIJKLMNOPQRST";
        var cleaned = ConfigEventErrors.Sanitize($"connect to tls://nats.example:4222 with {seed} failed");
        Assert.DoesNotContain(seed, cleaned);
        Assert.DoesNotContain("tls://nats.example:4222", cleaned);
        Assert.Contains("[redacted]", cleaned);
    }
}
