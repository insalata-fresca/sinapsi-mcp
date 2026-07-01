using System.Text.Json.Nodes;
using Sshgw.Mcp;
using Xunit;

namespace Sshgw.Mcp.Tests;

/// <summary>
/// Load-bearing tool-level coverage for the "surfaced upstream output → structured,
/// scrubbed envelope" hardening path. The guard suite only ever exercises the
/// short-circuit (no transport reached); these tests drive a
/// <see cref="CannedTransport"/> PAST the guards so the tool bodies run:
///   - execute-command routes a credential on stderr through
///     <see cref="SshgwErrors.Sanitize"/> → the envelope shows <c>[redacted]</c>;
///   - the ok/exitCode verdict is computed on the RAW exit code FIRST, so a
///     redaction can never flip a success into a failure (or vice-versa);
///   - read_file's successfully-requested <c>content</c> is returned VERBATIM (the
///     path denylist is the bound, not the scrubber) — a credential-shaped file the
///     policy cleared is NOT mangled;
///   - a transport timeout surfaces as a structured error, not an unhandled throw.
/// </summary>
public sealed class SshgwToolErrorTests : IDisposable
{
    private readonly string _dir;
    private readonly ServerRegistry _registry;
    private readonly SshgwOptions _opts;

    public SshgwToolErrorTests()
    {
        _dir = Directory.CreateTempSubdirectory("sshgw-err-").FullName;
        var registryPath = Path.Combine(_dir, "servers.json");
        // allow-all whitelist (no "whitelist") + a broad read_file allowlist so the
        // guards pass and the tool body runs against the canned transport.
        File.WriteAllText(registryPath, """
        [
          {
            "name": "alpha",
            "host": "alpha.example.com",
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

    // ── execute-command: stderr redaction + raw-exit-code verdict ─────────────

    [Fact]
    public async Task ExecuteCommand_SecretOnStderr_IsRedactedInEnvelope()
    {
        // A non-zero exit with a credential leaked on stderr.
        var canned = new CannedTransport(
            exec: new ExecResult(ExitCode: 3, Stdout: "partial output", Stderr: "connect failed\npassword=hunter2-supersecret"));

        var r = await SshgwTools.ExecuteCommand(_registry, canned, "alpha", "some-cmd", CancellationToken.None);

        var stderr = r["stderr"]!.GetValue<string>();
        Assert.Contains("connect failed", stderr);            // diagnostic survives
        Assert.DoesNotContain("hunter2-supersecret", stderr); // secret redacted
        Assert.Contains("[redacted]", stderr);
        // stdout is passed through untouched.
        Assert.Equal("partial output", r["stdout"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExecuteCommand_Verdict_IsRawExitCode_NotFlippedByRedaction()
    {
        // Exit 0 (success) even though stderr carries a credential. The ok/exitCode
        // verdict must be decided on the RAW exit code BEFORE the stderr scrub, so
        // ok stays true and exitCode stays 0.
        var canned = new CannedTransport(
            exec: new ExecResult(ExitCode: 0, Stdout: "ok", Stderr: "warning token=leaked.jwt.value"));

        var r = await SshgwTools.ExecuteCommand(_registry, canned, "alpha", "some-cmd", CancellationToken.None);

        Assert.True(Ok(r));
        Assert.Equal(0, r["exitCode"]!.GetValue<int>());
        var stderr = r["stderr"]!.GetValue<string>();
        Assert.DoesNotContain("leaked.jwt.value", stderr);
        Assert.Contains("[redacted]", stderr);
    }

    [Fact]
    public async Task ExecuteCommand_NonZeroExit_IsSurfacedAsNotOk()
    {
        var canned = new CannedTransport(
            exec: new ExecResult(ExitCode: 42, Stdout: "", Stderr: "no such file"));

        var r = await SshgwTools.ExecuteCommand(_registry, canned, "alpha", "some-cmd", CancellationToken.None);

        Assert.False(Ok(r));
        Assert.Equal(42, r["exitCode"]!.GetValue<int>());
        Assert.Equal("no such file", r["stderr"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExecuteCommand_EmptyStderr_IsNull()
    {
        var canned = new CannedTransport(
            exec: new ExecResult(ExitCode: 0, Stdout: "clean", Stderr: ""));

        var r = await SshgwTools.ExecuteCommand(_registry, canned, "alpha", "some-cmd", CancellationToken.None);

        Assert.True(Ok(r));
        Assert.Null(r["stderr"]);
    }

    // ── read_file: requested content is NOT scrubbed ──────────────────────────

    [Fact]
    public async Task ReadFile_RequestedContent_IsReturnedVerbatim_NotScrubbed()
    {
        // A file whose contents happen to look like a credential. Because the path
        // (/srv/app/settings.conf) cleared the allowlist policy, the bytes are the
        // deliberately-requested payload and must be returned verbatim — the path
        // denylist is the bound, not the error scrubber.
        const string body = "password=intentional-in-requested-file\nother=1\n";
        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        var canned = new CannedTransport(
            read: new ReadResult(bytes, TotalSize: bytes.Length, Truncated: false, NotFound: false));

        var r = await SshgwTools.ReadFile(_registry, canned, _opts, "alpha", "/srv/app/settings.conf", null, CancellationToken.None);

        Assert.True(Ok(r));
        var content = r["content"]!.GetValue<string>();
        Assert.Equal(body, content);                              // verbatim, not redacted
        Assert.Contains("intentional-in-requested-file", content);
        Assert.DoesNotContain("[redacted]", content);
        Assert.Equal("utf-8", r["encoding"]!.GetValue<string>());
    }

    [Fact]
    public async Task ReadFile_NotFound_IsStructuredError()
    {
        var canned = new CannedTransport(
            read: new ReadResult(Array.Empty<byte>(), 0, false, NotFound: true));

        var r = await SshgwTools.ReadFile(_registry, canned, _opts, "alpha", "/srv/missing", null, CancellationToken.None);

        Assert.False(Ok(r));
        Assert.Contains("not found", r["error"]!.GetValue<string>());
    }

    // ── timeout path surfaces as a structured error, not an unhandled throw ────

    [Fact]
    public async Task ExecuteCommand_TransportTimeout_SurfacesAsError_NotUnhandledThrow()
    {
        // The transport reports a timeout by surfacing a negative exit code and a
        // timeout marker on stderr (the shape SshClient produces on a killed run).
        var canned = new CannedTransport(
            exec: new ExecResult(ExitCode: -1, Stdout: "", Stderr: "command killed after 30000ms timeout"));

        var r = await SshgwTools.ExecuteCommand(_registry, canned, "alpha", "hang", CancellationToken.None);

        Assert.False(Ok(r));
        Assert.Equal(-1, r["exitCode"]!.GetValue<int>());
        Assert.Contains("timeout", r["stderr"]!.GetValue<string>());
    }

    [Fact]
    public async Task ReadFile_TransportThrowsTimeout_Propagates_AsOperationCanceled()
    {
        // If the SFTP read itself times out, the tool does not swallow it — the
        // cancellation surfaces to the host, which maps it to an MCP error. We
        // assert the tool does not silently return ok on a timed-out read.
        var canned = new CannedTransport(
            readThrows: new OperationCanceledException("sftp read timed out"));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            SshgwTools.ReadFile(_registry, canned, _opts, "alpha", "/srv/app/big.bin", null, CancellationToken.None));
    }
}
