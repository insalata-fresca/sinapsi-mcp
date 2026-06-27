using Sinapsi.AgentJwt;
using Xunit;

namespace Sinapsi.AgentJwt.Tests;

/// <summary>
/// Covers the env-binding contract of <see cref="AgentJwtOptions.FromEnvironment"/>:
/// neutral defaults on unset, override on set, and the JWT_TTL_MIN
/// positive-only fallback. Env access is serialised via a shared lock since the
/// process env is global mutable state.
/// </summary>
[Collection("env")]
public sealed class AgentJwtOptionsTests
{
    private static readonly string[] Vars =
        ["AGENT_KEY_DIR", "OIDC_ISSUER", "OIDC_AUDIENCE_PROJECT_ID", "JWT_TTL_MIN"];

    private static void ClearAll()
    {
        foreach (var v in Vars) Environment.SetEnvironmentVariable(v, null);
    }

    [Fact]
    public void FromEnvironment_Unset_UsesNeutralDefaults()
    {
        ClearAll();
        var o = AgentJwtOptions.FromEnvironment();

        Assert.Equal("/etc/agent-jwt/keys", o.KeyDir);
        Assert.Equal("https://oidc.example", o.Issuer);
        Assert.Equal("", o.AudienceProjectId);
        Assert.Equal(15, o.TtlMinutes);
    }

    [Fact]
    public void Defaults_AreWallClean_NoBakedTopology()
    {
        ClearAll();
        var o = AgentJwtOptions.FromEnvironment();

        // No real host/issuer/project baked into the neutral defaults.
        // The project-id assertion uses an obviously-synthetic sentinel — the
        // point is "default is not some non-empty project id", served by any
        // fake value; a real infrastructure id must never appear here.
        Assert.DoesNotContain("insalata-fresca", o.Issuer);
        Assert.DoesNotContain("auth.", o.Issuer);
        Assert.DoesNotContain("mcp-gateway", o.KeyDir);
        Assert.NotEqual("000000000000000000", o.AudienceProjectId);
    }

    [Fact]
    public void FromEnvironment_Set_OverridesDefaults()
    {
        ClearAll();
        Environment.SetEnvironmentVariable("AGENT_KEY_DIR", "/keys");
        Environment.SetEnvironmentVariable("OIDC_ISSUER", "https://id.test");
        Environment.SetEnvironmentVariable("OIDC_AUDIENCE_PROJECT_ID", "999");
        Environment.SetEnvironmentVariable("JWT_TTL_MIN", "30");
        try
        {
            var o = AgentJwtOptions.FromEnvironment();
            Assert.Equal("/keys", o.KeyDir);
            Assert.Equal("https://id.test", o.Issuer);
            Assert.Equal("999", o.AudienceProjectId);
            Assert.Equal(30, o.TtlMinutes);
        }
        finally { ClearAll(); }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("not-a-number")]
    [InlineData("")]
    public void FromEnvironment_NonPositiveOrInvalidTtl_FallsBackTo15(string raw)
    {
        ClearAll();
        Environment.SetEnvironmentVariable("JWT_TTL_MIN", raw);
        try
        {
            Assert.Equal(15, AgentJwtOptions.FromEnvironment().TtlMinutes);
        }
        finally { ClearAll(); }
    }

    [Fact]
    public void Ctor_Defaults_MatchFromEnvironmentDefaults()
    {
        var o = new AgentJwtOptions();
        Assert.Equal("/etc/agent-jwt/keys", o.KeyDir);
        Assert.Equal("https://oidc.example", o.Issuer);
        Assert.Equal("", o.AudienceProjectId);
        Assert.Equal(15, o.TtlMinutes);
    }
}

[CollectionDefinition("env", DisableParallelization = true)]
public sealed class EnvCollection { }
