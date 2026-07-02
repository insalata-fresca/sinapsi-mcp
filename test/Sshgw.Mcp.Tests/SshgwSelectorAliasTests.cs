using System.Text.Json.Nodes;
using Sshgw.Mcp;
using Xunit;

namespace Sshgw.Mcp.Tests;

/// <summary>
/// The harness advertises the target-selector as <c>server</c>, but the wire
/// contract is <c>connectionName</c>. These tests pin the ALIAS resolution added so
/// the C# server accepts BOTH names, fail-closed, without inventing a routing
/// default:
///   - <c>connectionName</c> (the wire contract) WINS when both are supplied;
///   - the <c>server</c> alias alone routes to that server;
///   - both absent ⇒ the tool's documented behavior (the literal 'default' server
///     for execute-command / upload, a clear "no server specified" error for
///     read_file — never a silent default connection);
///   - an unknown selector (under EITHER name) still hits the existing
///     "unknown server '…'" error;
///   - an explicitly-blank selector is a malformed value, not a fall-through.
///
/// A <see cref="CannedTransport"/> drives the tool bodies past the guards so the
/// resolved selector can be asserted end-to-end (which server entry was reached),
/// and a <see cref="ThrowingTransport"/> proves the fail-closed paths short-circuit
/// BEFORE any SSH I/O.
/// </summary>
public sealed class SshgwSelectorAliasTests : IDisposable
{
    private readonly string _dir;
    private readonly ServerRegistry _registry;
    private readonly SshgwOptions _opts;

    public SshgwSelectorAliasTests()
    {
        _dir = Directory.CreateTempSubdirectory("sshgw-alias-").FullName;
        var registryPath = Path.Combine(_dir, "servers.json");
        // Two allow-all servers so a command clears the whitelist and the tool body
        // runs against the canned transport, plus a 'default' server so the
        // both-absent fallback for execute-command / upload resolves. The per-server
        // host lets a test assert WHICH entry was reached (i.e. which selector won).
        File.WriteAllText(registryPath, """
        [
          {
            "name": "default",
            "host": "default.example.com",
            "username": "deploy",
            "privateKey": "/etc/sshgw/keys/id_ed25519",
            "readFilePolicy": { "allow": ["/srv/**"] }
          },
          {
            "name": "alpha",
            "host": "alpha.example.com",
            "username": "deploy",
            "privateKey": "/etc/sshgw/keys/id_ed25519",
            "readFilePolicy": { "allow": ["/srv/**"] }
          },
          {
            "name": "beta",
            "host": "beta.example.com",
            "username": "deploy",
            "privateKey": "/etc/sshgw/keys/id_ed25519",
            "readFilePolicy": { "allow": ["/srv/**"] }
          }
        ]
        """);
        _opts = ThrowingTransport.NeutralOpts() with { ServerRegistryPath = registryPath };
        _registry = new ServerRegistry(_opts);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static bool Ok(JsonObject o) => o["ok"]!.GetValue<bool>();
    private static string Err(JsonObject o) => o["error"]!.GetValue<string>();

    // A transport that records the ServerEntry it was invoked against, so a test can
    // assert which server the selector resolved to.
    private sealed class RecordingTransport : SshClient
    {
        public ServerEntry? ExecEntry { get; private set; }
        public ServerEntry? UploadEntry { get; private set; }
        public ServerEntry? ReadEntry { get; private set; }

        public RecordingTransport() : base(ThrowingTransport.NeutralOpts()) { }

        public override Task<ExecResult> ExecuteAsync(ServerEntry e, string command, int? timeoutMs, string? directory, CancellationToken ct)
        { ExecEntry = e; return Task.FromResult(new ExecResult(0, "ok", "")); }

        public override Task<ReadResult> ReadFileAsync(ServerEntry e, string path, int maxBytes, CancellationToken ct)
        { ReadEntry = e; var b = System.Text.Encoding.UTF8.GetBytes("body"); return Task.FromResult(new ReadResult(b, b.Length, false, false)); }

        public override Task<string?> ResolveRealPathAsync(ServerEntry e, string path, CancellationToken ct)
            => Task.FromResult<string?>(path);

        public override Task<UploadResult> UploadAsync(ServerEntry e, string localPath, string remotePath, CancellationToken ct)
        { UploadEntry = e; return Task.FromResult(new UploadResult(10, true, false)); }
    }

    // ── execute-command ───────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteCommand_ServerAlias_Alone_RoutesToThatServer()
    {
        var t = new RecordingTransport();
        var r = await SshgwTools.ExecuteCommand(_registry, t, cmdString: "anything", server: "alpha");
        Assert.True(Ok(r));
        Assert.Equal("alpha.example.com", t.ExecEntry!.Host); // the alias routed
    }

    [Fact]
    public async Task ExecuteCommand_ConnectionName_Wins_Over_ServerAlias()
    {
        var t = new RecordingTransport();
        // Both supplied and DIFFERENT — connectionName (the wire contract) must win.
        var r = await SshgwTools.ExecuteCommand(_registry, t, cmdString: "anything", connectionName: "alpha", server: "beta");
        Assert.True(Ok(r));
        Assert.Equal("alpha.example.com", t.ExecEntry!.Host);
    }

    [Fact]
    public async Task ExecuteCommand_BothAbsent_FallsBackTo_default_Server()
    {
        var t = new RecordingTransport();
        var r = await SshgwTools.ExecuteCommand(_registry, t, cmdString: "anything");
        Assert.True(Ok(r));
        Assert.Equal("default.example.com", t.ExecEntry!.Host);
    }

    [Fact]
    public async Task ExecuteCommand_UnknownServerAlias_HitsExistingError_NoSshIo()
    {
        var t = new ThrowingTransport();
        var r = await SshgwTools.ExecuteCommand(_registry, t, cmdString: "anything", server: "ghost");
        Assert.False(Ok(r));
        Assert.Equal("unknown server 'ghost'", Err(r));
        Assert.False(t.Reached);
    }

    [Fact]
    public async Task ExecuteCommand_BlankServerAlias_WithNoConnectionName_IsRejected_NoSshIo()
    {
        var t = new ThrowingTransport();
        var r = await SshgwTools.ExecuteCommand(_registry, t, cmdString: "anything", connectionName: null, server: "   ");
        Assert.False(Ok(r));
        Assert.Equal("connectionName is required", Err(r));
        Assert.False(t.Reached);
    }

    // ── upload ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Upload_ServerAlias_Alone_RoutesToThatServer()
    {
        var t = new RecordingTransport();
        var r = await SshgwTools.Upload(_registry, t, "/local/f", "/remote/f", server: "beta");
        Assert.True(Ok(r));
        Assert.Equal("beta.example.com", t.UploadEntry!.Host);
    }

    [Fact]
    public async Task Upload_ConnectionName_Wins_Over_ServerAlias()
    {
        var t = new RecordingTransport();
        var r = await SshgwTools.Upload(_registry, t, "/local/f", "/remote/f", connectionName: "alpha", server: "beta");
        Assert.True(Ok(r));
        Assert.Equal("alpha.example.com", t.UploadEntry!.Host);
    }

    [Fact]
    public async Task Upload_BothAbsent_FallsBackTo_default_Server()
    {
        var t = new RecordingTransport();
        var r = await SshgwTools.Upload(_registry, t, "/local/f", "/remote/f");
        Assert.True(Ok(r));
        Assert.Equal("default.example.com", t.UploadEntry!.Host);
    }

    [Fact]
    public async Task Upload_UnknownServerAlias_HitsExistingError_NoSshIo()
    {
        var t = new ThrowingTransport();
        var r = await SshgwTools.Upload(_registry, t, "/local/f", "/remote/f", server: "ghost");
        Assert.False(Ok(r));
        Assert.Equal("unknown server 'ghost'", Err(r));
        Assert.False(t.Reached);
    }

    // ── read_file ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadFile_ServerAlias_Alone_RoutesToThatServer()
    {
        var t = new RecordingTransport();
        var r = await SshgwTools.ReadFile(_registry, t, _opts, connectionName: null, remotePath: "/srv/f", max_bytes: null, server: "alpha", ct: CancellationToken.None);
        Assert.True(Ok(r));
        Assert.Equal("alpha.example.com", t.ReadEntry!.Host);
    }

    [Fact]
    public async Task ReadFile_ConnectionName_Wins_Over_ServerAlias()
    {
        var t = new RecordingTransport();
        var r = await SshgwTools.ReadFile(_registry, t, _opts, connectionName: "alpha", remotePath: "/srv/f", max_bytes: null, server: "beta", ct: CancellationToken.None);
        Assert.True(Ok(r));
        Assert.Equal("alpha.example.com", t.ReadEntry!.Host);
    }

    [Fact]
    public async Task ReadFile_BothAbsent_IsClearError_NoSshIo()
    {
        var t = new ThrowingTransport();
        var r = await SshgwTools.ReadFile(_registry, t, _opts, connectionName: null, remotePath: "/srv/f", max_bytes: null, server: null, ct: CancellationToken.None);
        Assert.False(Ok(r));
        Assert.Equal("no server specified: pass connectionName (or server)", Err(r));
        Assert.False(t.Reached);
    }

    [Fact]
    public async Task ReadFile_UnknownServerAlias_HitsExistingError_NoSshIo()
    {
        var t = new ThrowingTransport();
        var r = await SshgwTools.ReadFile(_registry, t, _opts, connectionName: null, remotePath: "/srv/f", max_bytes: null, server: "ghost", ct: CancellationToken.None);
        Assert.False(Ok(r));
        Assert.Equal("unknown server 'ghost'", Err(r));
        Assert.False(t.Reached);
    }
}
