using Sshgw.Mcp;
using Xunit;

namespace Sshgw.Mcp.Tests;

/// <summary>
/// Unit tests for <see cref="SshgwValidation"/>: every tool parameter is checked
/// here BEFORE the whitelist/policy bound and BEFORE any SSH I/O. These assert
/// that valid input passes (returns null) and that each rejection reason is
/// produced for the matching malformed input.
///
/// NOTE: NUL is expressed with the C# escape "\0" in InlineData — never a literal
/// NUL byte — so these source files diff and view cleanly as plain text.
/// </summary>
public sealed class SshgwValidationTests
{
    // ── connectionName ───────────────────────────────────────────────────────
    [Theory]
    [InlineData("alpha")]
    [InlineData("beta-01")]
    [InlineData("prod_node.2")]
    public void ValidateConnectionName_AcceptsValid(string name) =>
        Assert.Null(SshgwValidation.ValidateConnectionName(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateConnectionName_RejectsEmpty(string? name) =>
        Assert.Equal("connectionName is required", SshgwValidation.ValidateConnectionName(name));

    [Fact]
    public void ValidateConnectionName_RejectsTooLong()
    {
        var name = new string('a', SshgwValidation.MaxConnectionNameLength + 1);
        Assert.Contains("too long", SshgwValidation.ValidateConnectionName(name)!);
    }

    [Theory]
    [InlineData("bad\nname")]
    [InlineData("bad\tname")]
    [InlineData("bad\rname")]
    [InlineData("bad\0name")]   // C# escape \0 == NUL, not a literal NUL byte
    public void ValidateConnectionName_RejectsControlChars(string name) =>
        Assert.Contains("control characters", SshgwValidation.ValidateConnectionName(name)!);

    // ── cmdString ────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("uptime")]
    [InlineData("df -h")]
    [InlineData("cat /etc/os-release")]
    [InlineData("systemctl status nginx")]
    public void ValidateCommand_AcceptsValid(string cmd) =>
        Assert.Null(SshgwValidation.ValidateCommand(cmd));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateCommand_RejectsEmpty(string? cmd) =>
        Assert.Equal("cmdString is required", SshgwValidation.ValidateCommand(cmd));

    [Fact]
    public void ValidateCommand_RejectsTooLong()
    {
        var cmd = new string('x', SshgwValidation.MaxCommandLength + 1);
        Assert.Contains("too long", SshgwValidation.ValidateCommand(cmd)!);
    }

    [Theory]
    [InlineData("uptime\nrm -rf /")]   // newline-injected second command
    [InlineData("cmd\twith-tab")]
    [InlineData("cmd\rwith-cr")]
    [InlineData("cmd\0with-nul")]      // C# escape \0 == NUL, not a literal NUL byte
    public void ValidateCommand_RejectsControlChars(string cmd) =>
        Assert.Contains("control characters", SshgwValidation.ValidateCommand(cmd)!);

    // ── path (remotePath / localPath) ────────────────────────────────────────
    [Theory]
    [InlineData("/var/log/syslog")]
    [InlineData("/etc/os-release")]
    [InlineData("relative/still/passes/this/layer")]   // absoluteness is ReadFilePolicy's job, not this guard's
    public void ValidatePath_AcceptsValid(string path) =>
        Assert.Null(SshgwValidation.ValidatePath(path, "remotePath"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidatePath_RejectsEmpty_WithParamName(string? path) =>
        Assert.Equal("remotePath is required", SshgwValidation.ValidatePath(path, "remotePath"));

    [Fact]
    public void ValidatePath_UsesTheGivenParamName()
    {
        // The reason string echoes the caller-supplied param name so upload's two
        // path params produce distinct messages.
        Assert.Equal("localPath is required", SshgwValidation.ValidatePath("", "localPath"));
    }

    [Fact]
    public void ValidatePath_RejectsTooLong()
    {
        var path = "/" + new string('a', SshgwValidation.MaxPathLength);
        Assert.Contains("too long", SshgwValidation.ValidatePath(path, "remotePath")!);
    }

    [Theory]
    [InlineData("/var/log/\nsyslog")]
    [InlineData("/var/log/\tsyslog")]
    [InlineData("/var/log/\rsyslog")]
    [InlineData("/var/log/\0syslog")]   // C# escape \0 == NUL, not a literal NUL byte
    public void ValidatePath_RejectsControlChars(string path) =>
        Assert.Contains("control characters", SshgwValidation.ValidatePath(path, "remotePath")!);

    [Theory]
    [InlineData("-rf")]
    [InlineData("--force")]
    [InlineData("-/etc/passwd")]
    public void ValidatePath_RejectsLeadingDash(string path) =>
        Assert.Contains("must not start with '-'", SshgwValidation.ValidatePath(path, "remotePath")!);

    // ── directory (execute-command working dir) — TIGHTER than a free path ────
    // It becomes `cd -- <dir> && …`, so it must be absolute + metachar-free.

    [Fact]
    public void ValidateDirectory_NullOrEmpty_IsValid_MeansNoCdPrefix()
    {
        Assert.Null(SshgwValidation.ValidateDirectory(null));
        Assert.Null(SshgwValidation.ValidateDirectory(""));
    }

    [Theory]
    [InlineData("/srv")]
    [InlineData("/srv/app")]
    [InlineData("/var/log/nginx")]
    [InlineData("/opt/a-b_c.d/1")]
    public void ValidateDirectory_AcceptsAbsoluteMetacharFreePaths(string dir) =>
        Assert.Null(SshgwValidation.ValidateDirectory(dir));

    [Theory]
    [InlineData("relative/dir")]
    [InlineData("srv")]
    [InlineData("./srv")]
    public void ValidateDirectory_RejectsNonAbsolute(string dir) =>
        Assert.Contains("absolute", SshgwValidation.ValidateDirectory(dir)!);

    [Theory]
    [InlineData("/srv; rm -rf /")]     // ';' + space
    [InlineData("/srv && x")]          // '&' + space
    [InlineData("/srv|x")]             // '|'
    [InlineData("/srv/$(x)")]          // '$' '(' ')'
    [InlineData("/srv/`x`")]           // backtick
    [InlineData("/srv/a b")]           // space
    [InlineData("/srv/*")]             // glob '*'
    [InlineData("/srv/a?b")]           // glob '?'
    [InlineData("/srv/~root")]         // '~'
    [InlineData("/srv/a\\b")]          // backslash
    [InlineData("/srv/{a,b}")]         // brace expansion
    [InlineData("/srv/[a]")]           // bracket
    [InlineData("/srv/a>b")]           // redirection
    [InlineData("/srv/a<b")]           // redirection
    [InlineData("/srv/a#b")]           // comment
    [InlineData("/srv/a!b")]           // history expansion
    [InlineData("/srv/a\"b")]          // double quote
    [InlineData("/srv/a'b")]           // single quote
    public void ValidateDirectory_RejectsShellMetacharacters(string dir) =>
        Assert.Contains("metacharacters", SshgwValidation.ValidateDirectory(dir)!);

    [Theory]
    [InlineData("/srv/\ndir")]
    [InlineData("/srv/\tdir")]
    [InlineData("/srv/\0dir")]         // C# escape \0 == NUL
    public void ValidateDirectory_RejectsControlChars(string dir) =>
        Assert.NotNull(SshgwValidation.ValidateDirectory(dir));

    [Fact]
    public void ValidateDirectory_RejectsTooLong()
    {
        var dir = "/" + new string('a', SshgwValidation.MaxPathLength);
        Assert.Contains("too long", SshgwValidation.ValidateDirectory(dir)!);
    }
}
