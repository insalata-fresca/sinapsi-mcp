using Cervello.Watcher;
using Xunit;

namespace Cervello.Watcher.Tests;

/// <summary>
/// drive-watch — config half (M6-refine: gateway URL + watcher agent identity
/// replace the Google-SA / egress-proxy config this superseded).
/// </summary>
public sealed class ConfigAndProxyTests
{
    // All config tests read from a LOCAL env map (WatcherConfig.From) — NEVER the
    // process environment — so a fail-closed bad-value test cannot leak into a
    // parallel test's WatcherConfig.From(...). Test isolation is a property of the
    // injected source, not of ordering/parallel luck.
    private static Dictionary<string, string?> Env(params (string Key, string? Value)[] pairs)
    {
        var map = new Dictionary<string, string?>();
        foreach (var (k, v) in pairs) map[k] = v;
        return map;
    }

    [Fact]
    public void Default_gateway_url_is_the_local_agentgateway_mcp_endpoint()
    {
        var cfg = WatcherConfig.From(Env()); // empty map ⇒ all defaults
        Assert.Equal("http://127.0.0.1:8443/mcp", cfg.GatewayUrl);
    }

    [Fact]
    public void Bad_gateway_url_throws_naming_the_var()
    {
        var env = Env(("GATEWAY_URL", "not-a-url"));
        var ex = Assert.Throws<InvalidOperationException>(() => WatcherConfig.From(env));
        Assert.Contains("GATEWAY_URL", ex.Message);
    }

    [Fact]
    public void Default_watcher_agent_is_agent_cervello_watcher()
    {
        var cfg = WatcherConfig.From(Env());
        Assert.Equal("agent-cervello-watcher", cfg.WatcherAgent);
    }

    [Fact]
    public void Watcher_agent_is_overridable()
    {
        var env = Env(("CERVELLO_WATCHER_AGENT", "agent-cervello-watcher-staging"));
        var cfg = WatcherConfig.From(env);
        Assert.Equal("agent-cervello-watcher-staging", cfg.WatcherAgent);
    }

    [Fact]
    public void Bad_poll_interval_throws_naming_the_var()
    {
        var env = Env(("CERVELLO_WATCHER_POLL_INTERVAL_SECONDS", "0"));
        var ex = Assert.Throws<InvalidOperationException>(() => WatcherConfig.From(env));
        Assert.Contains("CERVELLO_WATCHER_POLL_INTERVAL_SECONDS", ex.Message);
    }

    [Fact]
    public void Non_numeric_health_port_throws_naming_the_var()
    {
        var env = Env(("CERVELLO_WATCHER_HEALTH_PORT", "abc"));
        var ex = Assert.Throws<InvalidOperationException>(() => WatcherConfig.From(env));
        Assert.Contains("CERVELLO_WATCHER_HEALTH_PORT", ex.Message);
    }

    [Fact]
    public void Default_folder_path_is_cervello_recordings()
    {
        var cfg = WatcherConfig.From(Env());
        Assert.Equal("cervello/recordings", cfg.FolderPath);
    }

    [Fact]
    public void Folder_id_is_unset_by_default_and_settable()
    {
        var defaultCfg = WatcherConfig.From(Env());
        Assert.Null(defaultCfg.FolderId);

        var env = Env(("CERVELLO_WATCHER_FOLDER_ID", "folder-abc123"));
        var cfg = WatcherConfig.From(env);
        Assert.Equal("folder-abc123", cfg.FolderId);
    }

    [Fact]
    public void Force_backfill_defaults_to_false()
    {
        var cfg = WatcherConfig.From(Env());
        Assert.False(cfg.ForceBackfill);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    public void Force_backfill_parses_well_formed_booleans(string raw, bool expected)
    {
        var env = Env(("CERVELLO_WATCHER_FORCE_BACKFILL", raw));
        var cfg = WatcherConfig.From(env);
        Assert.Equal(expected, cfg.ForceBackfill);
    }

    [Fact]
    public void Bad_force_backfill_throws_naming_the_var()
    {
        var env = Env(("CERVELLO_WATCHER_FORCE_BACKFILL", "yes-please"));
        var ex = Assert.Throws<InvalidOperationException>(() => WatcherConfig.From(env));
        Assert.Contains("CERVELLO_WATCHER_FORCE_BACKFILL", ex.Message);
    }
}
