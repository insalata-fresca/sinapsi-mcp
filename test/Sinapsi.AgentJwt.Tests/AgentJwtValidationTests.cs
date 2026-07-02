using Sinapsi.AgentJwt;
using Xunit;

namespace Sinapsi.AgentJwt.Tests;

// Plain-ASCII comment banner so this file diffs as TEXT.
//
// Hardening coverage for AgentJwtValidation: the public-API input matrix
// (agent name) and the fail-closed options matrix (missing/malformed required
// config -> throws naming the offending option). NUL is written with the C#
// escape \0 -- never a literal NUL byte -- so the source stays plain text.

public sealed class AgentJwtValidationTests
{
    // ---- ValidateAgent: accepts a well-formed name ------------------------

    [Theory]
    [InlineData("agent1")]
    [InlineData("claude-research")]
    [InlineData("a.b.c")]              // dots are fine inside a name; only "." / ".." are rejected
    [InlineData("Agent_42")]
    public void ValidateAgent_AcceptsWellFormedName(string agent)
    {
        Assert.Null(AgentJwtValidation.ValidateAgent(agent));
    }

    // ---- ValidateAgent: rejects malformed / hostile names -----------------

    [Fact]
    public void ValidateAgent_Null_Rejected()
    {
        Assert.Equal("agent is required", AgentJwtValidation.ValidateAgent(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateAgent_BlankRejected(string agent)
    {
        Assert.Equal("agent is required", AgentJwtValidation.ValidateAgent(agent));
    }

    [Fact]
    public void ValidateAgent_TooLongRejected()
    {
        var reason = AgentJwtValidation.ValidateAgent(new string('a', AgentJwtValidation.MaxAgentLength + 1));
        Assert.NotNull(reason);
        Assert.Contains("too long", reason);
    }

    [Theory]
    [InlineData("../etc/passwd")]       // path traversal
    [InlineData("sub/agent")]           // forward slash
    [InlineData("sub\\agent")]          // backslash
    public void ValidateAgent_PathSeparatorOrTraversalRejected(string agent)
    {
        Assert.NotNull(AgentJwtValidation.ValidateAgent(agent));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public void ValidateAgent_DotSegmentsRejected(string agent)
    {
        Assert.Equal("agent must not be '.' or '..'", AgentJwtValidation.ValidateAgent(agent));
    }

    [Theory]
    [InlineData("agent\0name")]         // embedded NUL (C# escape, never a literal byte)
    [InlineData("agent\nname")]         // newline
    [InlineData("agent\tname")]         // tab
    public void ValidateAgent_ControlCharsRejected(string agent)
    {
        Assert.Equal("agent contains control characters", AgentJwtValidation.ValidateAgent(agent));
    }

    // ---- ValidateOptions: a fully-valid options object passes -------------

    private static AgentJwtOptions Valid() => new()
    {
        KeyDir = "/keys",
        Issuer = "https://id.test",
        AudienceProjectId = "proj-123",
        TtlMinutes = 15,
    };

    [Fact]
    public void ValidateOptions_ValidOptions_DoesNotThrow()
    {
        AgentJwtValidation.ValidateOptions(Valid()); // no throw
    }

    // ---- ValidateOptions: fail-closed on missing / malformed required config

    [Fact]
    public void ValidateOptions_MissingKeyDir_ThrowsNamingIt()
    {
        var opt = new AgentJwtOptions { KeyDir = "", Issuer = "https://id.test", AudienceProjectId = "p" };
        var ex = Assert.Throws<ArgumentException>(() => AgentJwtValidation.ValidateOptions(opt));
        Assert.Contains("KeyDir", ex.Message);
        Assert.Contains("AGENT_KEY_DIR", ex.Message);
    }

    [Fact]
    public void ValidateOptions_MissingIssuer_ThrowsNamingIt()
    {
        var opt = new AgentJwtOptions { KeyDir = "/keys", Issuer = "", AudienceProjectId = "p" };
        var ex = Assert.Throws<ArgumentException>(() => AgentJwtValidation.ValidateOptions(opt));
        Assert.Contains("OIDC_ISSUER", ex.Message);
    }

    [Theory]
    [InlineData("id.test")]             // no scheme
    [InlineData("ftp://id.test")]       // wrong scheme
    [InlineData("not a url")]
    [InlineData("/relative/only")]
    public void ValidateOptions_NonHttpIssuer_ThrowsNamingIt(string issuer)
    {
        var opt = new AgentJwtOptions { KeyDir = "/keys", Issuer = issuer, AudienceProjectId = "p" };
        var ex = Assert.Throws<ArgumentException>(() => AgentJwtValidation.ValidateOptions(opt));
        Assert.Contains("OIDC_ISSUER", ex.Message);
    }

    [Fact]
    public void ValidateOptions_MissingAudience_ThrowsNamingIt()
    {
        var opt = new AgentJwtOptions { KeyDir = "/keys", Issuer = "https://id.test", AudienceProjectId = "" };
        var ex = Assert.Throws<ArgumentException>(() => AgentJwtValidation.ValidateOptions(opt));
        Assert.Contains("OIDC_AUDIENCE_PROJECT_ID", ex.Message);
    }

    [Fact]
    public void ValidateOptions_AudienceTooLong_ThrowsNamingIt()
    {
        var opt = new AgentJwtOptions
        {
            KeyDir = "/keys",
            Issuer = "https://id.test",
            AudienceProjectId = new string('9', AgentJwtValidation.MaxAudienceLength + 1),
        };
        var ex = Assert.Throws<ArgumentException>(() => AgentJwtValidation.ValidateOptions(opt));
        Assert.Contains("OIDC_AUDIENCE_PROJECT_ID", ex.Message);
        Assert.Contains("too long", ex.Message);
    }

    [Theory]
    [InlineData("proj\0id")]            // NUL (C# escape)
    [InlineData("proj\nid")]
    public void ValidateOptions_AudienceControlChars_ThrowsNamingIt(string audience)
    {
        var opt = new AgentJwtOptions { KeyDir = "/keys", Issuer = "https://id.test", AudienceProjectId = audience };
        var ex = Assert.Throws<ArgumentException>(() => AgentJwtValidation.ValidateOptions(opt));
        Assert.Contains("OIDC_AUDIENCE_PROJECT_ID", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]                     // below the min-2 floor
    [InlineData(-5)]
    [InlineData(AgentJwtOptions.MaxTtlMinutes + 1)]
    public void ValidateOptions_OutOfRangeTtl_ThrowsNamingIt(int ttl)
    {
        var opt = new AgentJwtOptions
        {
            KeyDir = "/keys",
            Issuer = "https://id.test",
            AudienceProjectId = "p",
            TtlMinutes = ttl,
        };
        var ex = Assert.Throws<ArgumentException>(() => AgentJwtValidation.ValidateOptions(opt));
        Assert.Contains("JWT_TTL_MIN", ex.Message);
    }
}
