using Microsoft.Extensions.Hosting;

namespace Sinapsi.SentinelConsole;

/// <summary>
/// Dev-only: when <c>SENTINEL_CONSOLE_DEMO=1</c>, feeds the read-model + live feed a small
/// stream of realistic decisions so the Console renders populated WITHOUT a live bus — for
/// a first look / screenshot before the sshgw/proxmox emitters are wired in prod. Off by
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
        (DeployEvent.KindReleased, "sinapsi-sentinel-console", "", "0.1.103", "sha256:9f2a1c4e7b80", ""),
        (DeployEvent.KindApplied, "sinapsi-sentinel-console", "132", "0.1.103", "sha256:9f2a1c4e7b80", "ok"),
        (DeployEvent.KindReleased, "sshgw-mcp", "", "0.1.87", "sha256:1122334455aa", ""),
        (DeployEvent.KindApplied, "sshgw-mcp", "121", "0.1.87", "sha256:1122334455aa", "ok"),
        (DeployEvent.KindReleased, "deploy-controller", "", "0.1.19", "sha256:aabbccddeeff", ""),
        (DeployEvent.KindFailed, "deploy-controller", "116", "0.1.19", "", "error: restart failed: exit 1"),
    };

    private static readonly (string layer, string tool, string server, string verb, string verdict, string reason, string cmd)[] Script =
    {
        ("q1","sshgw.execute-command","","", "allow","tuple: agent-cockpit-editor can_call sshgw.execute-command","" ),
        ("q2","sshgw.execute-command","bernex-proxmox","systemctl","requiresApproval","systemctl restart = mutating → requiresApproval","systemctl restart nats-server"),
        ("q3","sshgw.execute-command","","systemctl","requiresApproval","operator elevation (ask-gate)","systemctl restart nats-server"),
        ("q2","sshgw.execute-command","ct121-mcp-gateway","journalctl","allow","","journalctl -u sshgw-mcp --no-pager -n 40"),
        ("q2","sshgw.execute-command","ct139-brain","cat","deny","reads a secret-policy-blocked path","cat /etc/claude-brain-api/api.env"),
        ("q1","proxmox.execute_container_command","","", "allow","tuple: agent-nightly can_call proxmox.execute_container_command",""),
        ("q2","proxmox.execute_container_command","pve","pct","allow","","pct config 139"),
        ("q3","forgejo.merge_pull_request","","merge","requiresApproval","self-merge out of lane","merge #1417"),
        ("q2","sshgw.execute-command","genova-openwrt","wg","allow","","wg show wg0"),
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
