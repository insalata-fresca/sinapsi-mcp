using Sshgw.Mcp;
using Xunit;

namespace Sshgw.Mcp.Tests;

/// <summary>
/// The command whitelist is the in-MCP bound that keeps execute-command to a known
/// set of commands. The matcher must split on '|', compile each piece as its own
/// regex, treat an empty whitelist as allow-all, and never partial-match an
/// anchored pattern.
/// </summary>
public sealed class CommandWhitelistTests
{
    [Fact]
    public void Empty_or_whitespace_is_allow_all()
    {
        foreach (var spec in new string?[] { null, "", "   " })
        {
            var wl = new CommandWhitelist(spec);
            Assert.True(wl.AllowAll);
            Assert.True(wl.IsAllowed("anything goes"));
            Assert.True(wl.IsAllowed("rm -rf /"));
        }
    }

    [Fact]
    public void Anchored_pattern_matches_exactly()
    {
        var wl = new CommandWhitelist("^uptime$");
        Assert.False(wl.AllowAll);
        Assert.True(wl.IsAllowed("uptime"));
        Assert.False(wl.IsAllowed("uptime; rm -rf /"));
        Assert.False(wl.IsAllowed("do uptime"));
    }

    [Fact]
    public void Pipe_splits_into_independent_patterns()
    {
        var wl = new CommandWhitelist("^uptime$|^df -h$|^cat /etc/os-release$");
        Assert.True(wl.IsAllowed("uptime"));
        Assert.True(wl.IsAllowed("df -h"));
        Assert.True(wl.IsAllowed("cat /etc/os-release"));
        Assert.False(wl.IsAllowed("cat /etc/shadow"));
    }

    [Fact]
    public void Empty_command_against_a_real_whitelist_is_denied()
    {
        var wl = new CommandWhitelist("^uptime$");
        Assert.False(wl.IsAllowed(""));
    }

    [Fact]
    public void Pattern_with_argument_does_not_allow_appended_shell()
    {
        // An anchored pattern must not be defeated by chaining a second command.
        var wl = new CommandWhitelist("^systemctl status [a-z0-9@.-]+$");
        Assert.True(wl.IsAllowed("systemctl status nginx"));
        Assert.False(wl.IsAllowed("systemctl status nginx && cat /etc/passwd"));
    }
}
