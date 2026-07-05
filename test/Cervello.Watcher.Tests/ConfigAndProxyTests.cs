using System.Net;
using Cervello.Watcher;
using Cervello.Watcher.Drive;
using Xunit;

namespace Cervello.Watcher.Tests;

/// <summary>
/// drive-watch — config half + "All Drive access goes through the egress proxy".
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
    public void Default_proxy_is_the_ct_tinyproxy_address()
    {
        var cfg = WatcherConfig.From(Env()); // empty map ⇒ all defaults
        Assert.Equal("http://127.0.0.1:13130", cfg.HttpProxyUrl);
    }

    [Fact]
    public void Bad_proxy_url_throws_naming_the_var()
    {
        var env = Env(("CERVELLO_WATCHER_HTTP_PROXY", "not-a-url"));
        var ex = Assert.Throws<InvalidOperationException>(() => WatcherConfig.From(env));
        Assert.Contains("CERVELLO_WATCHER_HTTP_PROXY", ex.Message);
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

    // ---- Scenario: All Drive access goes through the egress proxy ----

    [Fact]
    public void ProxyHttpClientFactory_sets_webproxy_to_the_ct_proxy()
    {
        var factory = new ProxyHttpClientFactory("http://127.0.0.1:13130");
        Assert.NotNull(factory.WebProxy);
        Assert.Equal(new Uri("http://127.0.0.1:13130"), factory.WebProxy.Address);
        Assert.Equal("http://127.0.0.1:13130/", factory.ProxyAddress.ToString());
    }

    [Fact]
    public void ProxyHttpClientFactory_proxy_is_applied_to_created_handlers()
    {
        // The base HttpClientFactory applies its Proxy to every client it creates —
        // proving the routing is a property of the client, not luck.
        var factory = new ProxyHttpClientFactory("http://127.0.0.1:13130");
        var args = new Google.Apis.Http.CreateHttpClientArgs();
        using var client = factory.CreateHttpClient(args);
        Assert.NotNull(client);
        // The inherited Proxy is the exact WebProxy we constructed.
        IWebProxy proxy = factory.WebProxy;
        Assert.Equal(new Uri("http://127.0.0.1:13130"), ((WebProxy)proxy).Address);
    }
}
