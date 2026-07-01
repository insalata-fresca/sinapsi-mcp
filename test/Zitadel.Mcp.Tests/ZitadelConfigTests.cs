using Xunit;
using Zitadel.Mcp;

namespace Zitadel.Mcp.Tests;

/// <summary>
/// The Zitadel.Mcp host is configured entirely from the environment — a host pointed at any
/// ZITADEL deployment is this same binary configured differently. These tests pin the base
/// URL shaping, the required token (never baked in), the port default + override, and that
/// the guidance examples stay instance-neutral.
/// </summary>
public sealed class ZitadelConfigTests
{
    private static T WithEnv<T>(IReadOnlyDictionary<string, string?> env, Func<T> body)
    {
        var keys = new[] { "ZITADEL_BASE_URL", "ZITADEL_TOKEN", "ZITADEL_MCP_PORT", "ZITADEL_HTTP_TIMEOUT_MS", "AGENT_KEY_DIR" };
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
    public void Reads_base_url_and_token_and_defaults_the_port()
    {
        var cfg = WithEnv(new Dictionary<string, string?>
        {
            ["ZITADEL_BASE_URL"] = "https://auth.example.com",
            ["ZITADEL_TOKEN"] = "svc-token",
        }, ZitadelConfig.FromEnv);

        Assert.Equal("https://auth.example.com", cfg.BaseUrl);
        Assert.Equal("svc-token", cfg.Token);
        Assert.Equal(ZitadelConfig.DefaultPort, cfg.Port);
    }

    [Fact]
    public void Trailing_slash_on_base_url_is_normalised()
    {
        var cfg = WithEnv(new Dictionary<string, string?>
        {
            ["ZITADEL_BASE_URL"] = "https://auth.example.com/",
            ["ZITADEL_TOKEN"] = "t",
        }, ZitadelConfig.FromEnv);

        Assert.Equal("https://auth.example.com", cfg.BaseUrl);
    }

    [Fact]
    public void Port_is_overridable_from_env()
    {
        var cfg = WithEnv(new Dictionary<string, string?>
        {
            ["ZITADEL_BASE_URL"] = "https://auth.example.com",
            ["ZITADEL_TOKEN"] = "t",
            ["ZITADEL_MCP_PORT"] = "9333",
        }, ZitadelConfig.FromEnv);

        Assert.Equal(9333, cfg.Port);
    }

    [Fact]
    public void Missing_base_url_throws_with_a_neutral_example()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WithEnv(new Dictionary<string, string?>
            {
                ["ZITADEL_BASE_URL"] = null,
                ["ZITADEL_TOKEN"] = "t",
            }, ZitadelConfig.FromEnv));

        Assert.Contains("ZITADEL_BASE_URL", ex.Message);
        // The guidance example must stay instance-neutral — no real deployment baked in.
        Assert.Contains("auth.example.com", ex.Message);
    }

    [Fact]
    public void Missing_token_throws_and_insists_it_is_injected_not_baked_in()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WithEnv(new Dictionary<string, string?>
            {
                ["ZITADEL_BASE_URL"] = "https://auth.example.com",
                ["ZITADEL_TOKEN"] = null,
            }, ZitadelConfig.FromEnv));

        Assert.Contains("ZITADEL_TOKEN", ex.Message);
        Assert.Contains("baked in", ex.Message);
    }

    // ── port fail-closed ──────────────────────────────────────────────────────
    // Previously a non-numeric ZITADEL_MCP_PORT was silently swallowed by int.TryParse and
    // the default used — masking a typo'd port. It now fails startup naming the var.
    [Theory]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("70000")]
    public void Invalid_port_is_rejected_naming_the_var(string bad)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WithEnv(new Dictionary<string, string?>
            {
                ["ZITADEL_BASE_URL"] = "https://auth.example.com",
                ["ZITADEL_TOKEN"] = "t",
                ["ZITADEL_MCP_PORT"] = bad,
            }, ZitadelConfig.FromEnv));

        Assert.Contains("ZITADEL_MCP_PORT", ex.Message);
    }

    // ── http timeout: default + override + fail-closed clamp ───────────────────
    [Fact]
    public void Http_timeout_defaults_when_unset()
    {
        var cfg = WithEnv(new Dictionary<string, string?>
        {
            ["ZITADEL_BASE_URL"] = "https://auth.example.com",
            ["ZITADEL_TOKEN"] = "t",
        }, ZitadelConfig.FromEnv);

        Assert.Equal(ZitadelConfig.DefaultHttpTimeoutMs, cfg.HttpTimeoutMs);
    }

    [Fact]
    public void Http_timeout_is_overridable_from_env()
    {
        var cfg = WithEnv(new Dictionary<string, string?>
        {
            ["ZITADEL_BASE_URL"] = "https://auth.example.com",
            ["ZITADEL_TOKEN"] = "t",
            ["ZITADEL_HTTP_TIMEOUT_MS"] = "12345",
        }, ZitadelConfig.FromEnv);

        Assert.Equal(12345, cfg.HttpTimeoutMs);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-1")]
    public void Non_positive_or_non_numeric_http_timeout_is_rejected_naming_the_var(string bad)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WithEnv(new Dictionary<string, string?>
            {
                ["ZITADEL_BASE_URL"] = "https://auth.example.com",
                ["ZITADEL_TOKEN"] = "t",
                ["ZITADEL_HTTP_TIMEOUT_MS"] = bad,
            }, ZitadelConfig.FromEnv));

        Assert.Contains("ZITADEL_HTTP_TIMEOUT_MS", ex.Message);
    }

    [Fact]
    public void Absurdly_large_http_timeout_is_rejected()
    {
        var over = (ZitadelConfig.MaxHttpTimeoutMs + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Throws<InvalidOperationException>(() =>
            WithEnv(new Dictionary<string, string?>
            {
                ["ZITADEL_BASE_URL"] = "https://auth.example.com",
                ["ZITADEL_TOKEN"] = "t",
                ["ZITADEL_HTTP_TIMEOUT_MS"] = over,
            }, ZitadelConfig.FromEnv));
    }

    // ── agent-key-dir default ─────────────────────────────────────────────────
    [Fact]
    public void Agent_key_dir_defaults_when_unset()
    {
        var cfg = WithEnv(new Dictionary<string, string?>
        {
            ["ZITADEL_BASE_URL"] = "https://auth.example.com",
            ["ZITADEL_TOKEN"] = "t",
        }, ZitadelConfig.FromEnv);

        Assert.Equal(ZitadelConfig.DefaultAgentKeyDir, cfg.AgentKeyDir);
    }

    [Fact]
    public void Agent_key_dir_is_overridable_from_env()
    {
        var cfg = WithEnv(new Dictionary<string, string?>
        {
            ["ZITADEL_BASE_URL"] = "https://auth.example.com",
            ["ZITADEL_TOKEN"] = "t",
            ["AGENT_KEY_DIR"] = "/secrets/agent-keys",
        }, ZitadelConfig.FromEnv);

        Assert.Equal("/secrets/agent-keys", cfg.AgentKeyDir);
    }
}
