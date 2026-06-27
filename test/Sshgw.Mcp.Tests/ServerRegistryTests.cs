using Sshgw.Mcp;
using Xunit;

namespace Sshgw.Mcp.Tests;

/// <summary>
/// The registry parses the JSON server document, indexes entries by name, drops
/// nameless entries, and surfaces the optional read_file policy block.
/// </summary>
public sealed class ServerRegistryTests
{
    private const string Json = """
    [
      {
        "name": "alpha",
        "host": "alpha.example.com",
        "port": 2222,
        "username": "deploy",
        "privateKey": "/etc/sshgw/keys/id_ed25519",
        "whitelist": "^uptime$|^df -h$"
      },
      {
        "name": "beta",
        "host": "beta.example.com",
        "readFilePolicy": { "allow": ["/var/log/**"], "deny": ["/var/log/secret/**"] }
      },
      {
        "host": "nameless.example.com"
      }
    ]
    """;

    [Fact]
    public void Parses_and_indexes_by_name_dropping_nameless_entries()
    {
        var map = ServerRegistry.ParseJson(Json);
        Assert.Equal(2, map.Count);
        Assert.True(map.ContainsKey("alpha"));
        Assert.True(map.ContainsKey("beta"));
    }

    [Fact]
    public void Maps_scalar_fields_with_schema_defaults()
    {
        var map = ServerRegistry.ParseJson(Json);
        var alpha = map["alpha"];
        Assert.Equal("alpha.example.com", alpha.Host);
        Assert.Equal(2222, alpha.Port);
        Assert.Equal("deploy", alpha.Username);
        Assert.Equal("^uptime$|^df -h$", alpha.Whitelist);

        var beta = map["beta"];
        Assert.Equal(22, beta.Port);        // schema default
        Assert.Equal("root", beta.Username); // schema default
    }

    [Fact]
    public void Surfaces_the_optional_read_file_policy_block()
    {
        var map = ServerRegistry.ParseJson(Json);
        Assert.Null(map["alpha"].ReadFilePolicy);

        var policy = map["beta"].ReadFilePolicy;
        Assert.NotNull(policy);
        Assert.Equal(new[] { "/var/log/**" }, policy!.Allow);
        Assert.Equal(new[] { "/var/log/secret/**" }, policy.Deny);
    }

    [Fact]
    public void Registry_entry_drives_the_whitelist_matcher_end_to_end()
    {
        var map = ServerRegistry.ParseJson(Json);
        var wl = new CommandWhitelist(map["alpha"].Whitelist);
        Assert.True(wl.IsAllowed("uptime"));
        Assert.False(wl.IsAllowed("cat /etc/shadow"));
    }
}
