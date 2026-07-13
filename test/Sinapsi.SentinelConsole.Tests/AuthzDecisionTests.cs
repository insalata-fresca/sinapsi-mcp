using Sinapsi.SentinelConsole;
using Xunit;

namespace Sinapsi.SentinelConsole.Tests;

public sealed class AuthzDecisionTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Parses_Q2_CanonicalEnvelope()
    {
        var json = """
        {"specversion":"1.0","type":"x","source":"sshgw-mcp://authz/","time":"2026-07-13T09:41:22.000000Z",
         "data":{"layer":"q2","tool":"sshgw.execute-command","server":"bernex-proxmox",
                 "verb":"systemctl","verdict":"requiresApproval",
                 "reason":"systemctl restart = mutating","command":"systemctl restart nats",
                 "correlation_id":"req-9"}}
        """;
        var d = AuthzDecision.TryParse("homelab.security.authz.q2.requires-approval.cse", json, Now);
        Assert.NotNull(d);
        Assert.Equal("q2", d!.Layer);
        Assert.Equal("sshgw.execute-command", d.Tool);
        Assert.Equal("bernex-proxmox", d.Server);
        Assert.Equal("systemctl", d.Verb);
        Assert.Equal(AuthzDecision.VerdictApproval, d.Verdict);
        Assert.Equal("req-9", d.CorrelationId);
        Assert.Equal(2026, d.Time.Year);
        Assert.Equal(9, d.Time.Hour);                 // time from the envelope, not `now`
    }

    [Fact]
    public void Maps_Q3_AskGate_ToApproval()
    {
        var json = """
        {"time":"2026-07-13T10:00:00Z","data":{"tool":"mcp__homelab__sshgw_execute-command",
          "command":"systemctl restart foo","mode":"shadow","surface":"cse"}}
        """;
        var d = AuthzDecision.TryParse("homelab.security.ask-gate.shadow.cse", json, Now);
        Assert.NotNull(d);
        Assert.Equal("q3", d!.Layer);
        Assert.Equal(AuthzDecision.VerdictApproval, d.Verdict);
        Assert.Equal("systemctl", d.Verb);            // first token extracted
    }

    [Fact]
    public void Maps_Q3_DenyFloor_ToDeny()
    {
        var json = """{"data":{"tool":"Bash","command":"rm -rf /etc","matched":"rm-recursive"}}""";
        var d = AuthzDecision.TryParse("homelab.security.deny-floor.blocked.cse", json, Now);
        Assert.NotNull(d);
        Assert.Equal("q3", d!.Layer);
        Assert.Equal(AuthzDecision.VerdictDeny, d.Verdict);
        Assert.Equal("rm-recursive", d.Reason);
    }

    [Fact]
    public void Q2_MissingVerdict_IsDropped()
        => Assert.Null(AuthzDecision.TryParse(
            "homelab.security.authz.q2.allow.cse",
            """{"data":{"tool":"sshgw.execute-command"}}""", Now));

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"data":null}""")]
    public void UnparseableOrUnknown_IsDropped(string json)
        => Assert.Null(AuthzDecision.TryParse("homelab.other.subject", json, Now));

    [Fact]
    public void MissingTime_FallsBackToNow()
    {
        var d = AuthzDecision.TryParse(
            "homelab.security.authz.q2.allow.cse",
            """{"data":{"verdict":"allow","tool":"t"}}""", Now);
        Assert.NotNull(d);
        Assert.Equal(Now, d!.Time);
    }
}
