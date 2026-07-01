using Sshgw.Mcp;
using Xunit;

namespace Sshgw.Mcp.Tests;

/// <summary>
/// Hermetic coverage of the REAL <see cref="SshClient.UploadAsync"/> local-side
/// handling (gap 2): the concrete transport's local-file guard is exercised without
/// any network. A missing local file short-circuits to a structured not-found result
/// BEFORE any SFTP connection is opened, so the check is provably local. (The remote
/// SFTP leg is covered at the tool level via the injected transport in
/// <see cref="SshgwParityFeatureTests"/>, and end-to-end against the live host at
/// SHADOW time.)
/// </summary>
public sealed class SshClientUploadTests : IDisposable
{
    private readonly string _dir;
    private readonly SshClient _client;

    public SshClientUploadTests()
    {
        _dir = Directory.CreateTempSubdirectory("sshgw-upload-").FullName;
        _client = new SshClient(ThrowingTransport.NeutralOpts());
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static ServerEntry Entry() => new()
    {
        Name = "h",
        Host = "unreachable.invalid",   // never contacted on the not-found path
        Username = "u",
        PrivateKey = "/does/not/matter",
    };

    [Fact]
    public async Task UploadAsync_MissingLocalFile_ReturnsNotFound_WithoutOpeningSftp()
    {
        // The file does not exist → the concrete UploadAsync returns NotFound before
        // building a PrivateKeyFile or opening any SFTP connection. If it tried to
        // connect it would throw (unreachable host / missing key) rather than return
        // a clean NotFound — so a clean NotFound proves the local guard fired first.
        var missing = Path.Combine(_dir, "no-such-file.bin");
        var res = await _client.UploadAsync(Entry(), missing, "/remote/dest", CancellationToken.None);

        Assert.True(res.NotFound);
        Assert.False(res.Ok);
        Assert.Equal(0, res.BytesSent);
    }

    // ── ShellSingleQuote: the realpath-probe injection guard ──────────────────

    [Theory]
    [InlineData("/srv/plain", "'/srv/plain'")]
    [InlineData("/srv/a b", "'/srv/a b'")]                       // space → still one arg
    [InlineData("/srv/$(reboot)", "'/srv/$(reboot)'")]          // substitution neutralised
    [InlineData("/srv/`reboot`", "'/srv/`reboot`'")]            // backticks neutralised
    [InlineData("/srv/a;rm -rf /", "'/srv/a;rm -rf /'")]        // ';' neutralised
    public void ShellSingleQuote_wraps_and_neutralises_metacharacters(string input, string expected) =>
        Assert.Equal(expected, SshClient.ShellSingleQuote(input));

    [Fact]
    public void ShellSingleQuote_escapes_embedded_single_quotes()
    {
        // The one char special inside '…' is ' itself → close, escaped-quote, reopen.
        Assert.Equal("'a'\\''b'", SshClient.ShellSingleQuote("a'b"));
        // A crafted breakout attempt stays inside one literal argument.
        Assert.Equal("'/srv/x'\\''; reboot #'", SshClient.ShellSingleQuote("/srv/x'; reboot #"));
    }
}
