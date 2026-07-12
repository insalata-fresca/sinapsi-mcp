using Sshgw.Mcp;
using Xunit;
using D = Sshgw.Mcp.CommandAuthorizer.AuthDecision;

namespace Sshgw.Mcp.Tests;

/// <summary>
/// Unit tests for <see cref="CommandAuthorizer"/> (proposal 26). The corpus of
/// sample commands mirrors the shapes measured from the live S46 refusal stream:
/// diagnostic reads (fixed, piped, flag-permuted) must ALLOW; mutating commands must
/// route to RequiresApproval; secret-path reads and side-effecting shell constructs
/// must DENY. The authorizer is constructed with the DEFAULT (denylist) ReadFilePolicy
/// so the global secret denylist is in force.
/// </summary>
public sealed class CommandAuthorizerTests
{
    private static CommandAuthorizer Auth() =>
        new(new ReadFilePolicy(cfg: null));   // default global secret denylist

    private static D Decide(string cmd) => Auth().Authorize(cmd).Decision;

    // ── reads: fixed shapes ──────────────────────────────────────────────────
    [Theory]
    [InlineData("systemctl status nats-server")]
    [InlineData("systemctl list-units --type=service --state=failed")]
    [InlineData("systemctl --no-pager list-units --all --type=service")]
    [InlineData("journalctl -u sinapsi-orchestrator --no-pager -n 40")]
    [InlineData("journalctl -u nats-server --since \"2026-06-27 17:00\" --no-pager")]
    [InlineData("podman ps -a --format \"{{.Names}} {{.Status}}\"")]
    [InlineData("podman images --digests")]
    [InlineData("podman inspect systemd-bridge-mcp --format '{{.Image}}'")]
    [InlineData("docker ps --format '{{.Names}}'")]
    [InlineData("ps axo pcpu,pmem,comm --sort=-pcpu")]
    [InlineData("ls -la /opt/mcps/forgejo-mcp")]
    [InlineData("git -C /opt/mcps/proxmox-mcp remote -v")]
    [InlineData("git log --oneline -20")]
    [InlineData("ip route get 10.42.0.1")]
    [InlineData("ss -tlnp")]
    [InlineData("which python3 ffmpeg curl")]
    [InlineData("getent hosts api.anthropic.com")]
    [InlineData("nproc")]
    [InlineData("systemd-cgtop -bn1 --order=tasks")]
    [InlineData("curl -sS -m 6 http://10.42.0.138:8080/healthz")]
    [InlineData("skopeo inspect --raw docker://forgejo.insalata-fresca.ch/ste/sshgw-mcp:latest")]
    public void Reads_Allow(string cmd) => Assert.Equal(D.Allow, Decide(cmd));

    // ── reads: pipes + flag permutations (the legacy matcher could not express) ─
    [Theory]
    [InlineData("top -bn1 | head -25")]
    [InlineData("ps aux --sort=-%cpu | head -15")]
    [InlineData("journalctl -u sinapsi-orchestrator --no-pager -n 40 | grep -iE \"error|exception\"")]
    [InlineData("systemctl list-unit-files --type=service | grep -E \"mcp\"")]
    [InlineData("journalctl -u foo -n 40 -r --no-pager")]      // flags in any order
    [InlineData("journalctl --no-pager -r -n 40 -u foo")]      // same, permuted
    public void PipedAndPermuted_Reads_Allow(string cmd) => Assert.Equal(D.Allow, Decide(cmd));

    // ── writes → RequiresApproval (not a dead end) ───────────────────────────
    [Theory]
    [InlineData("systemctl restart nats-server")]
    [InlineData("systemctl start brain-workspace-manager.service")]
    [InlineData("podman restart sinapsi-orchestrator")]
    [InlineData("podman exec sinapsi-orchestrator env")]
    [InlineData("git commit -m wip")]
    [InlineData("git -C /srv/repo push origin main")]
    [InlineData("ip route add default via 10.42.0.1")]
    [InlineData("rm -f /tmp/x")]
    [InlineData("find /var/log -name '*.log' -delete")]
    [InlineData("curl -X POST http://10.42.0.121:9106/mcp")]
    [InlineData("curl -sO http://example/f")]                  // bundled short -O = write
    [InlineData("wget --post-data=x http://h/")]
    [InlineData("env")]                                        // secret dump, no path arg
    [InlineData("nft list ruleset")]
    public void Writes_RequireApproval(string cmd) => Assert.Equal(D.RequiresApproval, Decide(cmd));

    // ── secret-path reads → Deny (must stay blocked) ─────────────────────────
    [Theory]
    [InlineData("cat /etc/claude-brain-api/api.env")]
    [InlineData("grep BRAIN_BEARER_TOKEN /etc/claude-brain-api/api.env")]
    [InlineData("cat /etc/mcp-gateway/proxmox-mcp.json")]
    [InlineData("cat /root/.ssh/id_ed25519")]
    [InlineData("head -5 /etc/sinapsi-indexer/config.env")]
    [InlineData("cat /var/lib/x/foo.seed")]
    [InlineData("journalctl -u x | grep -f /etc/agentgateway-watchdog/config.env")]  // secret in a pipe
    public void SecretPaths_Deny(string cmd) => Assert.Equal(D.Deny, Decide(cmd));

    // ── structural gate → Deny (chaining/redirection/subshell/expansion) ──────
    [Theory]
    [InlineData("ls; rm -rf /etc")]
    [InlineData("ls && reboot")]
    [InlineData("cat /etc/hostname > /tmp/x")]
    [InlineData("echo $(cat /etc/shadow)")]
    [InlineData("echo `id`")]
    [InlineData("systemctl status foo; systemctl restart foo")]
    [InlineData("cat \"$SECRET\"")]                            // expansion inside double quotes
    public void ShellConstructs_Deny(string cmd) => Assert.Equal(D.Deny, Decide(cmd));

    // ── unknown verb → Deny (fail-closed) ────────────────────────────────────
    [Theory]
    [InlineData("frobnicate --all")]
    [InlineData("xyzzy")]
    public void UnknownVerb_Deny(string cmd) => Assert.Equal(D.Deny, Decide(cmd));

    // ── pipe with a mutating stage still requires approval; with a secret still denies ─
    [Fact]
    public void Pipe_WithMutatingStage_RequiresApproval() =>
        Assert.Equal(D.RequiresApproval, Decide("journalctl -u foo -n 5 | systemctl restart bar"));

    [Fact]
    public void Deny_DominatesApproval_WhenBoth() =>
        // a mutating verb AND a secret read in one pipeline → the hard Deny wins.
        Assert.Equal(D.Deny, Decide("rm -f /tmp/x | cat /root/.ssh/id_ed25519"));

    [Fact]
    public void EmptyPipelineStage_Denies() => Assert.Equal(D.Deny, Decide("ls | | wc -l"));

    // ── the '|' inside a quoted regex is NOT a pipeline separator ─────────────
    [Fact]
    public void QuotedPipe_IsNotASeparator() =>
        Assert.Equal(D.Allow, Decide("grep -E \"error|warn|fatal\" /var/log/app.log"));

    // ── safe stream-redirections (write no file) are permitted ────────────────
    [Theory]
    [InlineData("systemctl is-active foo 2>/dev/null")]
    [InlineData("podman ps 2>/dev/null")]
    [InlineData("journalctl -u foo --no-pager 2>&1")]
    [InlineData("ls -la /opt >/dev/null")]
    [InlineData("journalctl -u foo 2>&1 | grep error")]
    [InlineData("systemctl status foo 2>/dev/null | head -5")]
    public void SafeStreamRedirections_Allow(string cmd) => Assert.Equal(D.Allow, Decide(cmd));

    // ── a redirection to a real file is still refused ─────────────────────────
    [Theory]
    [InlineData("cat /etc/hostname > /tmp/x")]
    [InlineData("echo hi >> /etc/motd")]
    [InlineData("ls 2>/tmp/err")]
    public void FileRedirection_Deny(string cmd) => Assert.Equal(D.Deny, Decide(cmd));
}
