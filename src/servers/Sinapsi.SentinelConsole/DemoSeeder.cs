using Microsoft.Extensions.Hosting;

namespace Sinapsi.SentinelConsole;

/// <summary>
/// Dev-only: when <c>SENTINEL_CONSOLE_DEMO=1</c>, feeds the read-model + live feed a small
/// stream of realistic decisions so the Console renders populated WITHOUT a live bus — for
/// a first look / screenshot before the live command/host emitters are wired in prod. Off by
/// default; never runs unless the env flag is set.
/// </summary>
public sealed class DemoSeeder : BackgroundService
{
    private readonly ReadModel _rm;
    private readonly LiveFeed _feed;
    private readonly DeployModel _dm;
    public DemoSeeder(ReadModel rm, LiveFeed feed, DeployModel dm) { _rm = rm; _feed = feed; _dm = dm; }

    // A small released->applied sequence per service, replayed on a slower cadence than the
    // authz script so the Deploys section also renders populated in the demo/first-look view.
    private static readonly (string kind, string svc, string ctid, string version, string digest, string result)[] DeployScript =
    {
        (DeployEvent.KindReleased, "sentinel-console", "", "0.1.103", "sha256:9f2a1c4e7b80", ""),
        (DeployEvent.KindApplied, "sentinel-console", "app-server", "0.1.103", "sha256:9f2a1c4e7b80", "ok"),
        (DeployEvent.KindReleased, "the-mcp-gateway", "", "0.1.87", "sha256:1122334455aa", ""),
        (DeployEvent.KindApplied, "the-mcp-gateway", "edge-host", "0.1.87", "sha256:1122334455aa", "ok"),
        (DeployEvent.KindReleased, "deploy-agent", "", "0.1.19", "sha256:aabbccddeeff", ""),
        (DeployEvent.KindFailed, "deploy-agent", "build-runner", "0.1.19", "", "error: restart failed: exit 1"),
    };

    private static readonly (string layer, string tool, string server, string verb, string verdict, string reason, string cmd)[] Script =
    {
        ("q1","ssh.run-command","","", "allow","tuple: operator-role can_call ssh.run-command","" ),
        ("q2","ssh.run-command","edge-host","systemctl","requiresApproval","systemctl restart = mutating → requiresApproval","systemctl restart the-event-bus"),
        ("q3","ssh.run-command","","systemctl","requiresApproval","operator elevation (ask-gate)","systemctl restart the-event-bus"),
        ("q2","ssh.run-command","app-server","journalctl","allow","","journalctl -u the-mcp-gateway --no-pager -n 40"),
        ("q2","ssh.run-command","build-runner","cat","deny","reads a secret-policy-blocked path","cat /etc/example-service/config.env"),
        ("q1","container.exec","","", "allow","tuple: batch-agent can_call container.exec",""),
        ("q2","container.exec","app-server","container.exec","allow","","container inspect web-01"),
        ("q3","repo.merge-request","","merge","requiresApproval","self-merge out of lane","merge request 42"),
        ("q2","ssh.run-command","edge-host","uptime","allow","","uptime"),
    };

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        int i = 0, req = 100, j = 0;
        while (!ct.IsCancellationRequested)
        {
            var s = Script[i % Script.Length];
            // group the first three (Q1→Q2→Q3 of the restart) under one correlation id so
            // the click-to-expand chain demonstrates the per-request join.
            var corr = (i % Script.Length) is 0 or 1 or 2 ? $"req-{req}" : $"req-{req}-{i % Script.Length}";
            var d = new AuthzDecision(s.layer, s.tool, s.server, s.verb, s.verdict, s.reason, s.cmd, corr, DateTimeOffset.UtcNow);
            _rm.Record(d);
            _feed.Publish(d);
            i++;
            if (i % Script.Length == 0) req += 7;

            // one deploy event every ~4 authz ticks, so the Deploys section fills in a bit
            // slower than the live feed — closer to real cadence.
            if (i % 4 == 0)
            {
                var ds = DeployScript[j % DeployScript.Length];
                _dm.Record(new DeployEvent(ds.kind, ds.svc, ds.ctid, ds.version, ds.digest, ds.result, DateTimeOffset.UtcNow));
                j++;
            }

            try { await Task.Delay(TimeSpan.FromMilliseconds(1400), ct); }
            catch (OperationCanceledException) { break; }
        }
    }
}
