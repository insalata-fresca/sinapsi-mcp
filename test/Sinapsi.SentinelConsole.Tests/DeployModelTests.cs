using Sinapsi.SentinelConsole;
using Xunit;

namespace Sinapsi.SentinelConsole.Tests;

public sealed class DeployModelTests
{
    private static DeployEvent Released(string svc, string version, string digest, int tick = 0)
        => new(DeployEvent.KindReleased, svc, "", version, digest, "",
               new DateTimeOffset(2026, 7, 13, 0, 0, tick % 60, TimeSpan.Zero));

    private static DeployEvent Applied(string svc, string ctid, string version, string digest, string result = "ok", int tick = 0)
        => new(DeployEvent.KindApplied, svc, ctid, version, digest, result,
               new DateTimeOffset(2026, 7, 13, 0, 0, tick % 60, TimeSpan.Zero));

    private static DeployEvent Failed(string svc, string ctid, string version, int tick = 0)
        => new(DeployEvent.KindFailed, svc, ctid, version, "", "error: restart failed",
               new DateTimeOffset(2026, 7, 13, 0, 0, tick % 60, TimeSpan.Zero));

    [Fact]
    public void State_TracksLatestReleasedAndApplied_PerService()
    {
        var dm = new DeployModel();
        dm.Record(Released("sinapsi-sentinel-console", "0.1.41", "sha256:aaa", tick: 1));
        dm.Record(Released("sinapsi-sentinel-console", "0.1.42", "sha256:bbb", tick: 2));
        dm.Record(Applied("sinapsi-sentinel-console", "132", "0.1.42", "sha256:bbb", tick: 3));

        var row = Assert.Single(dm.State());
        Assert.Equal("sinapsi-sentinel-console", row.Svc);
        Assert.Equal("0.1.42", row.LastReleasedVersion);      // latest release wins
        Assert.Equal("sha256:bbb", row.LastReleasedDigest);
        Assert.Equal("0.1.42", row.LastAppliedVersion);
        Assert.Equal("132", row.LastAppliedCtid);
        Assert.Equal(DeployEvent.KindApplied, row.LastResult);
    }

    [Fact]
    public void State_SeparatesServices()
    {
        var dm = new DeployModel();
        dm.Record(Released("svc-a", "0.1.1", "sha256:a"));
        dm.Record(Released("svc-b", "0.2.0", "sha256:b"));

        var rows = dm.State();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Svc == "svc-a" && r.LastReleasedVersion == "0.1.1");
        Assert.Contains(rows, r => r.Svc == "svc-b" && r.LastReleasedVersion == "0.2.0");
    }

    [Fact]
    public void State_TracksFailedApply_AsLastResult()
    {
        var dm = new DeployModel();
        dm.Record(Applied("svc-a", "132", "0.1.1", "sha256:a", tick: 1));
        dm.Record(Failed("svc-a", "132", "0.1.2", tick: 2));

        var row = Assert.Single(dm.State());
        Assert.Equal(DeployEvent.KindFailed, row.LastResult);
        Assert.Equal("0.1.2", row.LastAppliedVersion);       // latest apply attempt wins, even if failed
    }

    [Fact]
    public void Recent_ReturnsNewestFirst_Bounded()
    {
        var dm = new DeployModel(capacity: 3);
        for (int i = 0; i < 5; i++)
            dm.Record(Released($"svc-{i}", "0.1.1", "sha256:x", tick: i));

        var recent = dm.Recent(10);
        Assert.Equal(3, recent.Count);                        // capped at capacity
        Assert.Equal("svc-4", recent[0].Svc);                 // newest first
        Assert.Equal("svc-2", recent[2].Svc);                 // svc-0, svc-1 evicted
        Assert.Equal(5, dm.Total);                            // total still counts everything seen
    }

    [Fact]
    public void Recent_ClampsRequestedCount()
    {
        var dm = new DeployModel();
        dm.Record(Released("svc-a", "0.1.1", "sha256:x"));
        Assert.Empty(dm.Recent(0));
        Assert.Single(dm.Recent(5));
    }
}
