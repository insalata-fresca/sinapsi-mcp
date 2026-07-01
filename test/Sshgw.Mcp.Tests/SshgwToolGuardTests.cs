using System.Text.Json.Nodes;
using Sshgw.Mcp;
using Xunit;

namespace Sshgw.Mcp.Tests;

/// <summary>
/// Tool-level guard coverage: every rejection path (input validation, the command
/// whitelist, the read_file denylist, an unknown server) must short-circuit BEFORE
/// any SSH I/O. Each test injects a <see cref="ThrowingTransport"/> that throws the
/// instant a transport method is reached — so a passing test PROVES the guard fired
/// first and no connection was attempted.
/// </summary>
public sealed class SshgwToolGuardTests : IDisposable
{
    private readonly string _dir;
    private readonly ServerRegistry _registry;
    private readonly SshgwOptions _opts;

    public SshgwToolGuardTests()
    {
        // A registry with one whitelisted server (read-only) and one allowlist-
        // read_file server, written to a temp file the registry loads at ctor.
        _dir = Directory.CreateTempSubdirectory("sshgw-guard-").FullName;
        var registryPath = Path.Combine(_dir, "servers.json");
        File.WriteAllText(registryPath, """
        [
          {
            "name": "alpha",
            "host": "alpha.example.com",
            "username": "deploy",
            "privateKey": "/etc/sshgw/keys/id_ed25519",
            "whitelist": "^uptime$|^df -h$",
            "readFilePolicy": { "allow": ["/var/log/**"] }
          }
        ]
        """);
        _opts = ThrowingTransport.NeutralOpts() with { ServerRegistryPath = registryPath };
        _registry = new ServerRegistry(_opts);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static string Err(JsonObject o) => o["error"]!.GetValue<string>();
    private static bool Ok(JsonObject o) => o["ok"]!.GetValue<bool>();

    // ── execute-command ──────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteCommand_InvalidConnectionName_ShortCircuits_NoSshIo()
    {
        var t = new ThrowingTransport();
        var r = await SshgwTools.ExecuteCommand(_registry, t, "", "uptime", CancellationToken.None);
        Assert.False(Ok(r));
        Assert.Equal("connectionName is required", Err(r));
        Assert.False(t.Reached);
    }

    [Fact]
    public async Task ExecuteCommand_InvalidCommand_NewlineInjection_ShortCircuits_NoSshIo()
    {
        var t = new ThrowingTransport();
        var r = await SshgwTools.ExecuteCommand(_registry, t, "alpha", "uptime\nrm -rf /", CancellationToken.None);
        Assert.False(Ok(r));
        Assert.Contains("control characters", Err(r));
        Assert.False(t.Reached);
    }

    [Fact]
    public async Task ExecuteCommand_EmptyCommand_ShortCircuits_NoSshIo()
    {
        var t = new ThrowingTransport();
        var r = await SshgwTools.ExecuteCommand(_registry, t, "alpha", "   ", CancellationToken.None);
        Assert.False(Ok(r));
        Assert.Equal("cmdString is required", Err(r));
        Assert.False(t.Reached);
    }

    [Fact]
    public async Task ExecuteCommand_UnknownServer_ShortCircuits_NoSshIo()
    {
        var t = new ThrowingTransport();
        var r = await SshgwTools.ExecuteCommand(_registry, t, "ghost", "uptime", CancellationToken.None);
        Assert.False(Ok(r));
        Assert.Contains("unknown server", Err(r));
        Assert.False(t.Reached);
    }

    [Fact]
    public async Task ExecuteCommand_NotWhitelisted_ShortCircuits_NoSshIo()
    {
        // A validation-clean but not-whitelisted command must still be rejected
        // before any SSH I/O.
        var t = new ThrowingTransport();
        var r = await SshgwTools.ExecuteCommand(_registry, t, "alpha", "cat /etc/shadow", CancellationToken.None);
        Assert.False(Ok(r));
        Assert.Contains("whitelist", Err(r));
        Assert.False(t.Reached);
    }

    // ── read_file ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadFile_InvalidConnectionName_ShortCircuits_NoSshIo()
    {
        var t = new ThrowingTransport();
        var r = await SshgwTools.ReadFile(_registry, t, _opts, null!, "/var/log/syslog", null, CancellationToken.None);
        Assert.False(Ok(r));
        Assert.Equal("connectionName is required", Err(r));
        Assert.False(t.Reached);
    }

    [Fact]
    public async Task ReadFile_LeadingDashPath_ShortCircuits_NoSshIo()
    {
        var t = new ThrowingTransport();
        var r = await SshgwTools.ReadFile(_registry, t, _opts, "alpha", "-/etc/passwd", null, CancellationToken.None);
        Assert.False(Ok(r));
        Assert.Contains("must not start with '-'", Err(r));
        Assert.False(t.Reached);
    }

    [Fact]
    public async Task ReadFile_ControlCharPath_ShortCircuits_NoSshIo()
    {
        var t = new ThrowingTransport();
        var r = await SshgwTools.ReadFile(_registry, t, _opts, "alpha", "/var/log/\nsyslog", null, CancellationToken.None);
        Assert.False(Ok(r));
        Assert.Contains("control characters", Err(r));
        Assert.False(t.Reached);
    }

    [Fact]
    public async Task ReadFile_NotInAllowlist_ShortCircuits_NoSshIo()
    {
        // Validation-clean absolute path, but the allowlist server only permits
        // /var/log/** — /etc/hosts must be rejected before any SSH I/O.
        var t = new ThrowingTransport();
        var r = await SshgwTools.ReadFile(_registry, t, _opts, "alpha", "/etc/hosts", null, CancellationToken.None);
        Assert.False(Ok(r));
        Assert.Contains("allowlist", Err(r));
        Assert.False(t.Reached);
    }

    [Fact]
    public async Task ReadFile_UnknownServer_ShortCircuits_NoSshIo()
    {
        var t = new ThrowingTransport();
        var r = await SshgwTools.ReadFile(_registry, t, _opts, "ghost", "/var/log/syslog", null, CancellationToken.None);
        Assert.False(Ok(r));
        Assert.Contains("unknown server", Err(r));
        Assert.False(t.Reached);
    }

    // ── upload (stub still validates) ─────────────────────────────────────────

    [Fact]
    public void Upload_InvalidConnectionName_RejectedBeforeStub()
    {
        var r = SshgwTools.Upload("", "/local/f", "/remote/f");
        Assert.False(Ok(r));
        Assert.Equal("connectionName is required", Err(r));
    }

    [Fact]
    public void Upload_LeadingDashLocalPath_Rejected()
    {
        var r = SshgwTools.Upload("alpha", "-rf", "/remote/f");
        Assert.False(Ok(r));
        Assert.Contains("localPath", Err(r));
        Assert.Contains("must not start with '-'", Err(r));
    }

    [Fact]
    public void Upload_CleanArgs_ReachesStub()
    {
        // Validation-clean args fall through to the deliberate not-implemented stub.
        var r = SshgwTools.Upload("alpha", "/local/f", "/remote/f");
        Assert.False(Ok(r));
        Assert.Contains("not yet implemented", Err(r));
    }
}
