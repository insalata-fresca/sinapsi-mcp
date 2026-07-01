using Sinapsi.AgentJwt;
using Xunit;

namespace Sinapsi.AgentJwt.Tests;

/// <summary>
/// Covers the env-binding contract of <see cref="AgentJwtOptions.FromEnvironment"/>:
/// neutral defaults on unset, override on set, and the JWT_TTL_MIN fail-closed
/// bound (a non-numeric / out-of-range value throws naming the var; in-range
/// binds; empty == unset). Env access is serialised via the "env" collection
/// since the process env is global mutable state.
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

    // FAIL-CLOSED: a non-numeric or out-of-range JWT_TTL_MIN now THROWS naming
    // the env var, rather than silently swallowing a footgun to the 15-min
    // default. (Previously "0"/"-5"/"not-a-number" fell back silently — a
    // misconfigured deploy that meant to set a real TTL would never learn it was
    // ignored.) The exception message names JWT_TTL_MIN so the operator can fix it.
    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1")]                 // below the min-2 floor (cache margin is TTL-1)
    [InlineData("1441")]              // above the 24 h ceiling
    [InlineData("not-a-number")]
    [InlineData("15.5")]              // not an integer
    public void FromEnvironment_NonNumericOrOutOfRangeTtl_ThrowsNamingTheVar(string raw)
    {
        ClearAll();
        Environment.SetEnvironmentVariable("JWT_TTL_MIN", raw);
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => AgentJwtOptions.FromEnvironment());
            Assert.Contains("JWT_TTL_MIN", ex.Message);
        }
        finally { ClearAll(); }
    }

    // An empty JWT_TTL_MIN is indistinguishable from unset on .NET (setting a var
    // to "" removes it), so it correctly falls back to the neutral default.
    [Fact]
    public void FromEnvironment_EmptyTtl_FallsBackToDefault()
    {
        ClearAll();
        Environment.SetEnvironmentVariable("JWT_TTL_MIN", "");
        try
        {
            Assert.Equal(15, AgentJwtOptions.FromEnvironment().TtlMinutes);
        }
        finally { ClearAll(); }
    }

    // The accepted-range boundaries bind cleanly.
    [Theory]
    [InlineData("2", 2)]
    [InlineData("1440", 1440)]
    public void FromEnvironment_InRangeTtl_Binds(string raw, int expected)
    {
        ClearAll();
        Environment.SetEnvironmentVariable("JWT_TTL_MIN", raw);
        try
        {
            Assert.Equal(expected, AgentJwtOptions.FromEnvironment().TtlMinutes);
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
