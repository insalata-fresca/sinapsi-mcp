using Sshgw.Mcp;
using Xunit;

namespace Sshgw.Mcp.Tests;

/// <summary>
/// ReadFilePolicy is the in-MCP bound that keeps read_file from returning a secret.
/// It canonicalises the path lexically, rejects '..' escapes, applies the global
/// secret denylist in denylist mode, and switches to deny-by-default in allowlist
/// mode. Evaluate returns null when allowed, a reason string when blocked.
/// </summary>
public sealed class ReadFilePolicyTests
{
    private static ReadFilePolicy Denylist() => new(null);

    [Fact]
    public void Relative_path_is_rejected()
    {
        var r = Denylist().Evaluate("etc/hosts", out _);
        Assert.NotNull(r);
    }

    [Fact]
    public void Dotdot_escape_above_root_is_rejected()
    {
        var r = Denylist().Evaluate("/a/../../etc/passwd", out _);
        Assert.NotNull(r);
        Assert.Contains("escapes root", r);
    }

    [Fact]
    public void Dotdot_within_root_canonicalises_then_evaluates()
    {
        var r = Denylist().Evaluate("/var/log/../log/syslog", out var canonical);
        Assert.Null(r);
        Assert.Equal("/var/log/syslog", canonical);
    }

    [Theory]
    [InlineData("/etc/ssl/private/server.key")]
    [InlineData("/home/deploy/.ssh/id_ed25519")]
    [InlineData("/opt/app/config.env")]
    [InlineData("/var/lib/app/app.sqlite")]
    [InlineData("/etc/app/secrets/token")]
    [InlineData("/srv/tls/cert.pem")]
    public void Global_secret_denylist_blocks_known_secret_paths(string path)
    {
        var r = Denylist().Evaluate(path, out _);
        Assert.NotNull(r);
        Assert.Contains("denylist", r);
    }

    [Fact]
    public void Ordinary_path_is_allowed_in_denylist_mode()
    {
        var r = Denylist().Evaluate("/var/log/syslog", out var canonical);
        Assert.Null(r);
        Assert.Equal("/var/log/syslog", canonical);
    }

    [Fact]
    public void Allow_override_re_permits_a_generated_conf_under_a_sensitive_tree()
    {
        // A generated web-server conf under an nginx tree is re-allowed even though
        // broad denies could otherwise catch sibling files.
        var r = Denylist().Evaluate("/data/nginx/proxy_host/1.conf", out _);
        Assert.Null(r);
    }

    [Fact]
    public void Allowlist_mode_is_deny_by_default()
    {
        var policy = new ReadFilePolicy(new ReadFilePolicyConfig { Allow = new[] { "/var/log/**" } });
        Assert.Null(policy.Evaluate("/var/log/syslog", out _));        // in allowlist
        Assert.NotNull(policy.Evaluate("/etc/hosts", out _));          // not in allowlist
    }

    [Fact]
    public void Allowlist_mode_still_honours_extra_deny_globs()
    {
        var policy = new ReadFilePolicy(new ReadFilePolicyConfig
        {
            Allow = new[] { "/var/log/**" },
            Deny = new[] { "/var/log/secret/**" },
        });
        Assert.Null(policy.Evaluate("/var/log/syslog", out _));
        Assert.NotNull(policy.Evaluate("/var/log/secret/token", out _));
    }

    [Fact]
    public void Glob_single_star_does_not_cross_directory_separator()
    {
        // '*' must not match '/', so an allow of /var/log/* matches one segment only.
        var policy = new ReadFilePolicy(new ReadFilePolicyConfig { Allow = new[] { "/var/log/*" } });
        Assert.Null(policy.Evaluate("/var/log/syslog", out _));
        Assert.NotNull(policy.Evaluate("/var/log/nginx/access.log", out _));
    }
}
