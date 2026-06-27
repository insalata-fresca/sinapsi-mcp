using Metabase.Mcp;
using Xunit;

namespace Metabase.Mcp.Tests;

/// <summary>
/// The Metabase.Mcp host is configured entirely from the environment — a host pointed at any
/// Metabase deployment is this same binary configured differently. These tests pin the base
/// URL shaping, the required API key (never baked in), the port default + override, and that
/// the guidance examples stay instance-neutral.
/// </summary>
public sealed class MetabaseConfigTests
{
    private static T WithEnv<T>(IReadOnlyDictionary<string, string?> env, Func<T> body)
    {
        var keys = new[] { "METABASE_BASE_URL", "METABASE_API_KEY", "METABASE_MCP_PORT" };
        var saved = keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var k in keys) Environment.SetEnvironmentVariable(k, env.TryGetValue(k, out var v) ? v : null);
            return body();
        }
        finally
        {
            foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v);
        }
    }

    [Fact]
    public void Reads_base_url_and_api_key_and_defaults_the_port()
    {
        var cfg = WithEnv(new Dictionary<string, string?>
        {
            ["METABASE_BASE_URL"] = "https://metrics.example.com",
            ["METABASE_API_KEY"] = "mb-key",
        }, MetabaseConfig.FromEnv);

        Assert.Equal("https://metrics.example.com", cfg.BaseUrl);
        Assert.Equal("mb-key", cfg.ApiKey);
        Assert.Equal(MetabaseConfig.DefaultPort, cfg.Port);
    }

    [Fact]
    public void Trailing_slash_on_base_url_is_normalised()
    {
        var cfg = WithEnv(new Dictionary<string, string?>
        {
            ["METABASE_BASE_URL"] = "https://metrics.example.com/",
            ["METABASE_API_KEY"] = "k",
        }, MetabaseConfig.FromEnv);

        Assert.Equal("https://metrics.example.com", cfg.BaseUrl);
    }

    [Fact]
    public void Port_is_overridable_from_env()
    {
        var cfg = WithEnv(new Dictionary<string, string?>
        {
            ["METABASE_BASE_URL"] = "https://metrics.example.com",
            ["METABASE_API_KEY"] = "k",
            ["METABASE_MCP_PORT"] = "9444",
        }, MetabaseConfig.FromEnv);

        Assert.Equal(9444, cfg.Port);
    }

    [Fact]
    public void Missing_base_url_throws_with_a_neutral_example()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WithEnv(new Dictionary<string, string?>
            {
                ["METABASE_BASE_URL"] = null,
                ["METABASE_API_KEY"] = "k",
            }, MetabaseConfig.FromEnv));

        Assert.Contains("METABASE_BASE_URL", ex.Message);
        // The guidance example must stay instance-neutral — no real deployment baked in.
        Assert.Contains("metrics.example.com", ex.Message);
    }

    [Fact]
    public void Missing_api_key_throws_and_insists_it_is_injected_not_baked_in()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WithEnv(new Dictionary<string, string?>
            {
                ["METABASE_BASE_URL"] = "https://metrics.example.com",
                ["METABASE_API_KEY"] = null,
            }, MetabaseConfig.FromEnv));

        Assert.Contains("METABASE_API_KEY", ex.Message);
        Assert.Contains("baked in", ex.Message);
    }
}
