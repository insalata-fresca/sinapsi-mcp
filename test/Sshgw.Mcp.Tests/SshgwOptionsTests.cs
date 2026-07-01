using Sshgw.Mcp;
using Xunit;

namespace Sshgw.Mcp.Tests;

/// <summary>
/// <see cref="SshgwOptions.FromEnvironment"/> is fail-closed: every numeric option
/// has a neutral default AND a hard ceiling. Unset ⇒ default; a valid in-range
/// override is honoured; a non-integer / &lt;= 0 / above-ceiling value — or a
/// default byte cap that exceeds the hard byte cap — THROWS an
/// <see cref="System.InvalidOperationException"/> naming the offending env var,
/// rather than being silently swapped for the default (the old behaviour). These
/// mutate process env, so they run serially within one collection.
/// </summary>
[Collection("sshgw-env")]
public sealed class SshgwOptionsTests : IDisposable
{
    private static readonly string[] Keys =
    {
        "SSHGW_CONFIG_FILE", "SSHGW_CONNECT_TIMEOUT_MS", "SSHGW_COMMAND_TIMEOUT_MS",
        "SSHGW_READFILE_DEFAULT_MAX_BYTES", "SSHGW_READFILE_HARD_MAX_BYTES",
    };

    public SshgwOptionsTests() => Clear();
    public void Dispose() => Clear();
    private static void Clear() { foreach (var k in Keys) Environment.SetEnvironmentVariable(k, null); }

    // ── defaults + valid overrides ───────────────────────────────────────────

    [Fact]
    public void Defaults_apply_when_unset()
    {
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
        Environment.SetEnvironmentVariable("SSHGW_CONFIG_FILE", "/custom/servers.json");
        Environment.SetEnvironmentVariable("SSHGW_CONNECT_TIMEOUT_MS", "5000");
        Environment.SetEnvironmentVariable("SSHGW_READFILE_HARD_MAX_BYTES", "1048576");

        var o = SshgwOptions.FromEnvironment();
        Assert.Equal("/custom/servers.json", o.ServerRegistryPath);
        Assert.Equal(5_000, o.ConnectTimeoutMs);
        Assert.Equal(1_048_576, o.ReadFileHardMaxBytes);
        Assert.Equal(30_000, o.CommandTimeoutMs); // untouched default
    }

    [Fact]
    public void Every_numeric_var_is_bound_from_env()
    {
        Environment.SetEnvironmentVariable("SSHGW_CONNECT_TIMEOUT_MS", "1111");
        Environment.SetEnvironmentVariable("SSHGW_COMMAND_TIMEOUT_MS", "2222");
        Environment.SetEnvironmentVariable("SSHGW_READFILE_DEFAULT_MAX_BYTES", "3333");
        Environment.SetEnvironmentVariable("SSHGW_READFILE_HARD_MAX_BYTES", "44444");

        var o = SshgwOptions.FromEnvironment();
        Assert.Equal(1111, o.ConnectTimeoutMs);
        Assert.Equal(2222, o.CommandTimeoutMs);
        Assert.Equal(3333, o.ReadFileDefaultMaxBytes);
        Assert.Equal(44444, o.ReadFileHardMaxBytes);
    }

    // ── fail-closed: non-integer ─────────────────────────────────────────────
    // The old behaviour silently swapped a bad value for the default. It now
    // throws naming the offending var, so a config typo stops startup instead of
    // running with a plausible-but-unintended bound.

    [Theory]
    [InlineData("SSHGW_CONNECT_TIMEOUT_MS")]
    [InlineData("SSHGW_COMMAND_TIMEOUT_MS")]
    [InlineData("SSHGW_READFILE_DEFAULT_MAX_BYTES")]
    [InlineData("SSHGW_READFILE_HARD_MAX_BYTES")]
    public void Non_integer_value_is_rejected_naming_the_var(string var)
    {
        Environment.SetEnvironmentVariable(var, "not-a-number");
        var ex = Assert.Throws<InvalidOperationException>(() => SshgwOptions.FromEnvironment());
        Assert.Contains(var, ex.Message);
    }

    // ── fail-closed: <= 0 ────────────────────────────────────────────────────

    [Theory]
    [InlineData("SSHGW_CONNECT_TIMEOUT_MS", "0")]
    [InlineData("SSHGW_CONNECT_TIMEOUT_MS", "-1")]
    [InlineData("SSHGW_COMMAND_TIMEOUT_MS", "0")]
    [InlineData("SSHGW_COMMAND_TIMEOUT_MS", "-5000")]
    [InlineData("SSHGW_READFILE_DEFAULT_MAX_BYTES", "0")]
    [InlineData("SSHGW_READFILE_DEFAULT_MAX_BYTES", "-100")]
    [InlineData("SSHGW_READFILE_HARD_MAX_BYTES", "0")]
    [InlineData("SSHGW_READFILE_HARD_MAX_BYTES", "-1")]
    public void Non_positive_value_is_rejected_naming_the_var(string var, string bad)
    {
        Environment.SetEnvironmentVariable(var, bad);
        var ex = Assert.Throws<InvalidOperationException>(() => SshgwOptions.FromEnvironment());
        Assert.Contains(var, ex.Message);
    }

    // ── fail-closed: above the hard ceiling ──────────────────────────────────

    [Theory]
    [InlineData("SSHGW_CONNECT_TIMEOUT_MS", SshgwOptions.MaxConnectTimeoutMs)]
    [InlineData("SSHGW_COMMAND_TIMEOUT_MS", SshgwOptions.MaxCommandTimeoutMs)]
    [InlineData("SSHGW_READFILE_DEFAULT_MAX_BYTES", SshgwOptions.MaxReadFileDefaultMaxBytes)]
    [InlineData("SSHGW_READFILE_HARD_MAX_BYTES", SshgwOptions.MaxReadFileHardMaxBytes)]
    public void Above_ceiling_value_is_rejected_naming_the_var(string var, int ceiling)
    {
        Environment.SetEnvironmentVariable(
            var,
            ((long)ceiling + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var ex = Assert.Throws<InvalidOperationException>(() => SshgwOptions.FromEnvironment());
        Assert.Contains(var, ex.Message);
    }

    [Theory]
    [InlineData("SSHGW_CONNECT_TIMEOUT_MS", SshgwOptions.MaxConnectTimeoutMs)]
    [InlineData("SSHGW_COMMAND_TIMEOUT_MS", SshgwOptions.MaxCommandTimeoutMs)]
    public void At_the_exact_ceiling_is_accepted(string var, int ceiling)
    {
        Environment.SetEnvironmentVariable(
            var, ceiling.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // Exactly at the ceiling is the boundary that must still be honoured.
        var o = SshgwOptions.FromEnvironment();
        Assert.NotNull(o);
    }

    // ── fail-closed: cross-field (default cap > hard cap) ────────────────────

    [Fact]
    public void Default_cap_exceeding_hard_cap_is_rejected_naming_both_vars()
    {
        // Each value is independently valid (in 1..max), but the pair is
        // inconsistent: a caller-omitted max_bytes would ask for more than the hard
        // ceiling allows. Reject naming both env vars.
        Environment.SetEnvironmentVariable("SSHGW_READFILE_DEFAULT_MAX_BYTES", "1048576"); // 1 MiB
        Environment.SetEnvironmentVariable("SSHGW_READFILE_HARD_MAX_BYTES", "524288");     // 512 KiB

        var ex = Assert.Throws<InvalidOperationException>(() => SshgwOptions.FromEnvironment());
        Assert.Contains("SSHGW_READFILE_DEFAULT_MAX_BYTES", ex.Message);
        Assert.Contains("SSHGW_READFILE_HARD_MAX_BYTES", ex.Message);
    }

    [Fact]
    public void Default_cap_equal_to_hard_cap_is_accepted()
    {
        Environment.SetEnvironmentVariable("SSHGW_READFILE_DEFAULT_MAX_BYTES", "524288");
        Environment.SetEnvironmentVariable("SSHGW_READFILE_HARD_MAX_BYTES", "524288");

        var o = SshgwOptions.FromEnvironment();
        Assert.Equal(524_288, o.ReadFileDefaultMaxBytes);
        Assert.Equal(524_288, o.ReadFileHardMaxBytes);
    }
}
