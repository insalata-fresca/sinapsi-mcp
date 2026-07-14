using Sinapsi.SentinelConsole;
using Xunit;

namespace Sinapsi.SentinelConsole.Tests;

public sealed class DeployEventTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Parses_ReleasePublished_SvcFromSubject()
    {
        var json = """
        {"specversion":"1.0","type":"x","source":"ci:act_runner","time":"2026-07-13T09:41:22.000000Z",
         "data":{"version":"0.1.42","digest":"sha256:abc123def456","image":"forgejo.insalata-fresca.ch/ste/sinapsi-sentinel-console"}}
        """;
        var e = DeployEvent.TryParse("homelab.release.sinapsi-sentinel-console.published", json, Now);
        Assert.NotNull(e);
        Assert.Equal(DeployEvent.KindReleased, e!.Kind);
        Assert.Equal("sinapsi-sentinel-console", e.Svc);
        Assert.Equal("", e.Ctid);
        Assert.Equal("0.1.42", e.Version);
        Assert.Equal("sha256:abc123def456", e.Digest);
        Assert.Equal("", e.Result);
        Assert.Equal(2026, e.Time.Year);
        Assert.Equal(9, e.Time.Hour);              // time from the envelope, not `now`
    }

    [Fact]
    public void Parses_DeployApplied_FieldsFromPayload()
    {
        var json = """
        {"time":"2026-07-13T10:05:00Z",
         "data":{"ctid":"132","svc":"sinapsi-sentinel-console","version":"0.1.42",
                 "digest":"sha256:abc123def456","result":"ok","emitted_at":"2026-07-13T10:05:00Z"}}
        """;
        var e = DeployEvent.TryParse("homelab.deploy.132.sinapsi-sentinel-console.applied", json, Now);
        Assert.NotNull(e);
        Assert.Equal(DeployEvent.KindApplied, e!.Kind);
        Assert.Equal("sinapsi-sentinel-console", e.Svc);
        Assert.Equal("132", e.Ctid);
        Assert.Equal("0.1.42", e.Version);
        Assert.Equal("ok", e.Result);
    }

    [Fact]
    public void Parses_DeployFailed()
    {
        var json = """
        {"time":"2026-07-13T10:05:00Z",
         "data":{"ctid":"132","svc":"sinapsi-sentinel-console","version":"0.1.42",
                 "digest":"","result":"error: restart failed: exit 1"}}
        """;
        var e = DeployEvent.TryParse("homelab.deploy.132.sinapsi-sentinel-console.failed", json, Now);
        Assert.NotNull(e);
        Assert.Equal(DeployEvent.KindFailed, e!.Kind);
        Assert.StartsWith("error:", e.Result);
    }

    [Fact]
    public void DeployApplied_FallsBackToSubject_WhenPayloadOmitsCtidSvc()
    {
        var json = """{"data":{"version":"0.1.1","digest":"sha256:x","result":"ok"}}""";
        var e = DeployEvent.TryParse("homelab.deploy.121.sshgw-mcp.applied", json, Now);
        Assert.NotNull(e);
        Assert.Equal("121", e!.Ctid);
        Assert.Equal("sshgw-mcp", e.Svc);
    }

    [Fact]
    public void ReleasePublished_EmptySvc_IsDropped()
        => Assert.Null(DeployEvent.TryParse(
            "homelab.release..published", """{"data":{"version":"0.1.1"}}""", Now));

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"data":null}""")]
    public void UnparseableOrMissingData_IsDropped(string json)
        => Assert.Null(DeployEvent.TryParse("homelab.release.foo.published", json, Now));

    [Fact]
    public void UnrelatedSubject_IsDropped()
        => Assert.Null(DeployEvent.TryParse(
            "homelab.security.authz.q2.allow.cse", """{"data":{"tool":"t"}}""", Now));

    [Fact]
    public void MissingTime_FallsBackToNow()
    {
        var e = DeployEvent.TryParse(
            "homelab.release.foo.published", """{"data":{"version":"0.1.1"}}""", Now);
        Assert.NotNull(e);
        Assert.Equal(Now, e!.Time);
    }
}
