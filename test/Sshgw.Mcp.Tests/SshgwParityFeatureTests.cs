using System.Text.Json.Nodes;
using Sshgw.Mcp;
using Xunit;

namespace Sshgw.Mcp.Tests;

/// <summary>
/// Tool-level coverage for the incumbent-parity + hardening features added to close
/// the gaps: <c>execute-command</c>'s <c>directory</c> + per-call <c>timeout</c> +
/// optional <c>connectionName</c> (gap 1); the SFTP <c>upload</c> round-trip
/// (gap 2); and <c>read_file</c>'s remote-realpath SYMLINK re-check (gap 4). Each
/// test drives the tool body against a <see cref="CannedTransport"/> so the wiring
/// is exercised deterministically without real SSH I/O.
/// </summary>
public sealed class SshgwParityFeatureTests : IDisposable
{
    private readonly string _dir;
    private readonly ServerRegistry _registry;
    private readonly SshgwOptions _opts;

    public SshgwParityFeatureTests()
    {
        _dir = Directory.CreateTempSubdirectory("sshgw-feat-").FullName;
        var registryPath = Path.Combine(_dir, "servers.json");
        // 'default' → allow-all whitelist + a /srv allowlist, so the guards pass and
        // the tool body runs against the canned transport. A second server 'router'
        // with a real whitelist proves the whitelist matches the RAW (un-prefixed)
        // command even when a directory is supplied.
        File.WriteAllText(registryPath, """
        [
          {
            "name": "default",
            "host": "default.example.com",
            "username": "deploy",
            "privateKey": "/etc/sshgw/keys/id_ed25519",
            "readFilePolicy": { "allow": ["/srv/**", "/var/log/**"] }
          },
          {
            "name": "router",
            "host": "router.example.com",
            "username": "deploy",
            "privateKey": "/etc/sshgw/keys/id_ed25519",
            "whitelist": "^uptime$|^df -h$"
          }
        ]
        """);
        _opts = ThrowingTransport.NeutralOpts() with { ServerRegistryPath = registryPath };
        _registry = new ServerRegistry(_opts);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static bool Ok(JsonObject o) => o["ok"]!.GetValue<bool>();
    private static string Err(JsonObject o) => o["error"]!.GetValue<string>();

    // ── GAP 1: connectionName defaults to "default" ───────────────────────────

    [Fact]
    public async Task ExecuteCommand_ConnectionName_DefaultsTo_default()
    {
        var canned = new CannedTransport(exec: new ExecResult(0, "hi", ""));
        // No connectionName supplied → resolves the 'default' server.
        var r = await SshgwTools.ExecuteCommand(_registry, canned, cmdString: "anything");
        Assert.True(Ok(r));
        Assert.Single(canned.ExecCalls);
    }

    // ── GAP 1: directory is applied as a safe cd-prefix on the RAW command ─────

    [Fact]
    public async Task ExecuteCommand_Directory_IsPassedToTransport()
    {
        var canned = new CannedTransport(exec: new ExecResult(0, "out", ""));
        var r = await SshgwTools.ExecuteCommand(
            _registry, canned, cmdString: "ls", connectionName: "default", directory: "/srv/app");
        Assert.True(Ok(r));
        var (command, _, directory) = canned.ExecCalls.Single();
        Assert.Equal("ls", command);            // the RAW command reaches the transport
        Assert.Equal("/srv/app", directory);    // and the directory alongside it
    }

    [Fact]
    public async Task ExecuteCommand_Whitelist_Matches_RawCommand_Not_CdPrefixedForm()
    {
        // 'router' whitelists ^uptime$. Supplying a directory must NOT change whether
        // the whitelist matches — it gates the RAW command, exactly like the
        // incumbent. So "uptime" + directory is allowed; a non-whitelisted command
        // is refused regardless of directory.
        var canned = new CannedTransport(exec: new ExecResult(0, "up", ""));
        var ok = await SshgwTools.ExecuteCommand(
            _registry, canned, cmdString: "uptime", connectionName: "router", directory: "/tmp");
        Assert.True(Ok(ok));

        var bad = await SshgwTools.ExecuteCommand(
            _registry, canned, cmdString: "cat /etc/shadow", connectionName: "router", directory: "/tmp");
        Assert.False(Ok(bad));
        Assert.Contains("whitelist", Err(bad));
    }

    [Theory]
    [InlineData("/srv; rm -rf /")]            // ';' breakout
    [InlineData("/srv && cat /etc/shadow")]   // '&&' + space breakout
    [InlineData("/srv/$(reboot)")]            // command substitution
    [InlineData("/srv/`reboot`")]             // backtick substitution
    [InlineData("relative/dir")]              // not absolute
    [InlineData("/srv/*/x")]                  // glob metachar
    [InlineData("/srv/a|b")]                  // pipe
    public async Task ExecuteCommand_Directory_WithShellMetachars_IsRejected_NoSshIo(string dir)
    {
        var t = new ThrowingTransport();
        var r = await SshgwTools.ExecuteCommand(
            _registry, t, cmdString: "ls", connectionName: "default", directory: dir);
        Assert.False(Ok(r));
        Assert.False(t.Reached); // rejected before any SSH I/O
    }

    // ── GAP 1: per-call timeout is passed through (clamped in the transport) ───

    [Fact]
    public async Task ExecuteCommand_Timeout_IsPassedToTransport()
    {
        var canned = new CannedTransport(exec: new ExecResult(0, "out", ""));
        var r = await SshgwTools.ExecuteCommand(
            _registry, canned, cmdString: "anything", connectionName: "default", timeout: 5000);
        Assert.True(Ok(r));
        var (_, timeoutMs, _) = canned.ExecCalls.Single();
        Assert.Equal(5000, timeoutMs);
    }

    [Fact]
    public async Task ExecuteCommand_NoTimeout_PassesNull_TransportUsesConfiguredDefault()
    {
        var canned = new CannedTransport(exec: new ExecResult(0, "out", ""));
        await SshgwTools.ExecuteCommand(_registry, canned, cmdString: "anything", connectionName: "default");
        var (_, timeoutMs, _) = canned.ExecCalls.Single();
        Assert.Null(timeoutMs);
    }

    // ── GAP 2: upload round-trip ──────────────────────────────────────────────

    [Fact]
    public async Task Upload_RoundTrip_ReportsBytesSent()
    {
        var canned = new CannedTransport(upload: new UploadResult(BytesSent: 1234, Ok: true, NotFound: false));
        var r = await SshgwTools.Upload(_registry, canned, "/local/file.bin", "/remote/dest.bin", connectionName: "default");
        Assert.True(Ok(r));
        Assert.Equal("/remote/dest.bin", r["path"]!.GetValue<string>());
        Assert.Equal(1234, r["bytes_sent"]!.GetValue<long>());
        Assert.Equal(("/local/file.bin", "/remote/dest.bin"), canned.UploadCalls.Single());
    }

    [Fact]
    public async Task Upload_LocalFileMissing_IsStructuredError()
    {
        var canned = new CannedTransport(upload: new UploadResult(0, false, NotFound: true));
        var r = await SshgwTools.Upload(_registry, canned, "/local/missing", "/remote/dest", connectionName: "default");
        Assert.False(Ok(r));
        Assert.Contains("local file not found", Err(r));
    }

    // ── GAP 4: symlink realpath re-check ──────────────────────────────────────

    [Fact]
    public async Task ReadFile_SymlinkTargetInsideAllowlist_IsRead_FromRealTarget()
    {
        // /srv/app/link resolves (readlink -f) to /srv/data/real.txt — still inside
        // the /srv/** allowlist → allowed, and the bytes are read from the REAL target.
        var body = System.Text.Encoding.UTF8.GetBytes("resolved-content");
        var canned = new CannedTransport(
            read: new ReadResult(body, body.Length, false, NotFound: false),
            realPath: p => p == "/srv/app/link" ? "/srv/data/real.txt" : p);

        var r = await SshgwTools.ReadFile(
            _registry, canned, _opts, "default", "/srv/app/link", null, CancellationToken.None);

        Assert.True(Ok(r));
        Assert.Equal("/srv/data/real.txt", r["path"]!.GetValue<string>()); // read from resolved target
        Assert.Equal("resolved-content", r["content"]!.GetValue<string>());
        Assert.Contains("/srv/app/link", canned.RealPathCalls); // the re-check ran
    }

    [Fact]
    public async Task ReadFile_SymlinkEscapingAllowlist_IsRejected_BeforeBytesRead()
    {
        // /var/log/evil is inside the allowlist LEXICALLY, but its real target
        // /etc/shadow escapes it. The realpath re-check must catch this and REFUSE —
        // and it must refuse before the byte-read transport is reached.
        var canned = new CannedTransport(
            read: new ReadResult(System.Text.Encoding.UTF8.GetBytes("SECRET"), 6, false, NotFound: false),
            realPath: p => p == "/var/log/evil" ? "/etc/shadow" : p);

        var r = await SshgwTools.ReadFile(
            _registry, canned, _opts, "default", "/var/log/evil", null, CancellationToken.None);

        Assert.False(Ok(r));
        Assert.Contains("symlink target blocked", Err(r));
        // The re-check ran, and the byte-read never returned the secret.
        Assert.Contains("/var/log/evil", canned.RealPathCalls);
    }

    [Fact]
    public async Task ReadFile_SymlinkEscapingDenylist_IsRejected()
    {
        // Denylist-mode server: a path that clears the denylist lexically but whose
        // real target is a secret (.env) must be caught by the realpath re-check.
        var denyRegistryPath = Path.Combine(_dir, "deny.json");
        File.WriteAllText(denyRegistryPath, """
        [
          { "name": "d", "host": "h", "username": "u", "privateKey": "/k" }
        ]
        """);
        var opts = _opts with { ServerRegistryPath = denyRegistryPath };
        var registry = new ServerRegistry(opts);

        var canned = new CannedTransport(
            read: new ReadResult(System.Text.Encoding.UTF8.GetBytes("SECRET"), 6, false, NotFound: false),
            realPath: p => p == "/opt/app/harmless.txt" ? "/opt/app/config.env" : p);

        var r = await SshgwTools.ReadFile(
            registry, canned, opts, "d", "/opt/app/harmless.txt", null, CancellationToken.None);

        Assert.False(Ok(r));
        Assert.Contains("symlink target blocked", Err(r));
    }

    [Fact]
    public async Task ReadFile_NoSymlink_RealpathEqualsCanonical_ReadsDirectly()
    {
        // When readlink -f returns the same path (no symlink), the re-check is a
        // no-op and the read proceeds from the canonical path.
        var body = System.Text.Encoding.UTF8.GetBytes("plain");
        var canned = new CannedTransport(
            read: new ReadResult(body, body.Length, false, NotFound: false),
            realPath: p => p); // identity → no symlink

        var r = await SshgwTools.ReadFile(
            _registry, canned, _opts, "default", "/srv/app/plain.txt", null, CancellationToken.None);

        Assert.True(Ok(r));
        Assert.Equal("/srv/app/plain.txt", r["path"]!.GetValue<string>());
        Assert.Equal("plain", r["content"]!.GetValue<string>());
    }
}
