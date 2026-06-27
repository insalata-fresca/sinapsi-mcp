using Sshgw.Mcp;
using Xunit;

namespace Sshgw.Mcp.Tests;

/// <summary>
/// SshgwOptions.FromEnvironment falls back to neutral defaults when env vars are
/// unset and honours overrides when set.
/// </summary>
public sealed class SshgwOptionsTests
{
    private static readonly string[] Keys =
    {
        "SSHGW_CONFIG_FILE", "SSHGW_CONNECT_TIMEOUT_MS", "SSHGW_COMMAND_TIMEOUT_MS",
        "SSHGW_READFILE_DEFAULT_MAX_BYTES", "SSHGW_READFILE_HARD_MAX_BYTES",
    };

    private static void Clear() { foreach (var k in Keys) Environment.SetEnvironmentVariable(k, null); }

    [Fact]
    public void Defaults_apply_when_unset()
    {
        Clear();
        var o = SshgwOptions.FromEnvironment();
        Assert.Equal("/etc/sshgw/servers.json", o.ServerRegistryPath);
        Assert.Equal(10_000, o.ConnectTimeoutMs);
        Assert.Equal(30_000, o.CommandTimeoutMs);
        Assert.Equal(262_144, o.ReadFileDefaultMaxBytes);
        Assert.Equal(2_097_152, o.ReadFileHardMaxBytes);
    }

    [Fact]
    public void Env_overrides_are_honoured()
    {
        Clear();
        Environment.SetEnvironmentVariable("SSHGW_CONFIG_FILE", "/custom/servers.json");
        Environment.SetEnvironmentVariable("SSHGW_CONNECT_TIMEOUT_MS", "5000");
        Environment.SetEnvironmentVariable("SSHGW_READFILE_HARD_MAX_BYTES", "1048576");
        try
        {
            var o = SshgwOptions.FromEnvironment();
            Assert.Equal("/custom/servers.json", o.ServerRegistryPath);
            Assert.Equal(5_000, o.ConnectTimeoutMs);
            Assert.Equal(1_048_576, o.ReadFileHardMaxBytes);
            Assert.Equal(30_000, o.CommandTimeoutMs); // untouched default
        }
        finally { Clear(); }
    }

    [Fact]
    public void Non_integer_env_falls_back_to_the_default()
    {
        Clear();
        Environment.SetEnvironmentVariable("SSHGW_CONNECT_TIMEOUT_MS", "not-a-number");
        try
        {
            var o = SshgwOptions.FromEnvironment();
            Assert.Equal(10_000, o.ConnectTimeoutMs);
        }
        finally { Clear(); }
    }
}
