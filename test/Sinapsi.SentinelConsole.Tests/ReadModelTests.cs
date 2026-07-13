using Sinapsi.SentinelConsole;
using Xunit;

namespace Sinapsi.SentinelConsole.Tests;

public sealed class ReadModelTests
{
    private static AuthzDecision D(
        string layer, string tool, string verdict, string corr = "", string server = "",
        int tick = 0)
        => new(layer, tool, server, "verb", verdict, "reason", "cmd", corr,
               new DateTimeOffset(2026, 7, 13, 0, 0, tick % 60, TimeSpan.Zero));

    [Fact]
    public void Posture_CountsPerVerdict_AndTracksLatest()
    {
        var rm = new ReadModel();
        rm.Record(D("q2", "sshgw.execute-command", AuthzDecision.VerdictAllow, tick: 1));
        rm.Record(D("q2", "sshgw.execute-command", AuthzDecision.VerdictAllow, tick: 2));
        rm.Record(D("q2", "sshgw.execute-command", AuthzDecision.VerdictDeny, tick: 3));

        var row = Assert.Single(rm.Posture());
        Assert.Equal("sshgw.execute-command", row.Tool);
        Assert.Equal("q2", row.Layer);
        Assert.Equal(2, row.Allow);
        Assert.Equal(1, row.Deny);
        Assert.Equal(0, row.Approval);
        Assert.Equal(AuthzDecision.VerdictDeny, row.LastVerdict);   // latest wins
    }

    [Fact]
    public void Posture_SeparatesLayersAndTools()
    {
        var rm = new ReadModel();
        rm.Record(D("q2", "sshgw.execute-command", AuthzDecision.VerdictAllow));
        rm.Record(D("q3", "sshgw.execute-command", AuthzDecision.VerdictApproval));
        rm.Record(D("q2", "proxmox.execute_container_command", AuthzDecision.VerdictAllow));

        var rows = rm.Posture();
        Assert.Equal(3, rows.Count);               // (sshgw,q2) (sshgw,q3) (proxmox,q2)
        Assert.Contains(rows, r => r.Tool.StartsWith("proxmox") && r.Layer == "q2");
        Assert.Contains(rows, r => r.Tool.StartsWith("sshgw") && r.Layer == "q3");
    }

    [Fact]
    public void Recent_ReturnsNewestFirst_Bounded()
    {
        var rm = new ReadModel(capacity: 3);
        for (int i = 0; i < 5; i++)
            rm.Record(D("q2", "t", AuthzDecision.VerdictAllow, corr: $"c{i}", tick: i));

        var recent = rm.Recent(10);
        Assert.Equal(3, recent.Count);             // capped at capacity
        Assert.Equal("c4", recent[0].CorrelationId);   // newest first
        Assert.Equal("c2", recent[2].CorrelationId);   // c0,c1 evicted
        Assert.Equal(5, rm.Total);                 // total still counts everything seen
    }

    [Fact]
    public void Chain_JoinsByCorrelationId_OldestToNewest()
    {
        var rm = new ReadModel();
        rm.Record(D("q1", "sshgw.execute-command", AuthzDecision.VerdictAllow, corr: "req-1", tick: 3));
        rm.Record(D("q2", "sshgw.execute-command", AuthzDecision.VerdictApproval, corr: "req-1", tick: 5));
        rm.Record(D("q2", "sshgw.execute-command", AuthzDecision.VerdictAllow, corr: "other", tick: 4));

        var chain = rm.Chain("req-1");
        Assert.Equal(2, chain.Count);
        Assert.Equal("q1", chain[0].Layer);        // oldest first
        Assert.Equal("q2", chain[1].Layer);
    }

    [Fact]
    public void Chain_EmptyForUnknownOrBlank()
    {
        var rm = new ReadModel();
        rm.Record(D("q2", "t", AuthzDecision.VerdictAllow, corr: "x"));
        Assert.Empty(rm.Chain("nope"));
        Assert.Empty(rm.Chain(""));
    }
}
