using Forge.Mcp;
using Xunit;

namespace Forge.Mcp.Tests;

/// <summary>
/// The single Forge.Mcp host is configured per forge entirely from the environment — the
/// forgejo and codeberg deployments are this same binary, never forked code. These tests
/// pin that differentiation: the API base URL shaping, the required token, and the
/// FORGE_TOOLSETS opt-out that a Codeberg instance uses to drop tool groups it lacks.
/// </summary>
public sealed class ForgeConfigTests
{
    private static T WithEnv<T>(IReadOnlyDictionary<string, string?> env, Func<T> body)
    {
        var keys = new[] { "FORGE_NAME", "FORGE_BASE_URL", "FORGE_TOKEN", "FORGE_TOOLSETS", "FORGE_MCP_PORT" };
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

    // ── forgejo profile: full toolset, /api/v1/ suffix ──────────────────────────

    [Fact]
    public void Forgejo_profile_defaults_to_all_toolsets_and_suffixes_api_v1()
    {
        var cfg = WithEnv(new Dictionary<string, string?>
        {
            ["FORGE_NAME"] = "forgejo",
            ["FORGE_BASE_URL"] = "https://forge.example.com",
            ["FORGE_TOKEN"] = "pat-forgejo",
        }, ForgeConfig.FromEnv);

        Assert.Equal("forgejo", cfg.Name);
        Assert.Equal("https://forge.example.com/api/v1/", cfg.ApiBaseUrl);
        Assert.Equal("pat-forgejo", cfg.Token);
        Assert.Equal(9219, cfg.Port);
        // Full Gitea surface by default.
        Assert.True(cfg.Toolsets.Topics);
        Assert.True(cfg.Toolsets.Actions);
    }

    [Fact]
    public void BaseUrl_trailing_slash_is_normalised_before_the_api_suffix()
    {
        var cfg = WithEnv(new Dictionary<string, string?>
        {
            ["FORGE_BASE_URL"] = "https://forge.example.com/",
            ["FORGE_TOKEN"] = "t",
        }, ForgeConfig.FromEnv);

        Assert.Equal("https://forge.example.com/api/v1/", cfg.ApiBaseUrl);
    }

    // ── codeberg profile: same binary, different env, optional tools dropped ─────

    [Fact]
    public void Codeberg_profile_is_the_same_binary_with_topics_and_actions_opted_out()
    {
        var cfg = WithEnv(new Dictionary<string, string?>
        {
            ["FORGE_NAME"] = "codeberg",
            ["FORGE_BASE_URL"] = "https://codeberg.example.org",
            ["FORGE_TOKEN"] = "pat-codeberg",
            ["FORGE_TOOLSETS"] = "-topics,-actions",
        }, ForgeConfig.FromEnv);

        // Distinct identity + endpoint, but selected purely by configuration.
        Assert.Equal("codeberg", cfg.Name);
        Assert.Equal("https://codeberg.example.org/api/v1/", cfg.ApiBaseUrl);
        // The two optional groups a Codeberg instance can lack are switched off.
        Assert.False(cfg.Toolsets.Topics);
        Assert.False(cfg.Toolsets.Actions);
    }

    [Fact]
    public void Port_is_overridable_from_env()
    {
        var cfg = WithEnv(new Dictionary<string, string?>
        {
            ["FORGE_BASE_URL"] = "https://forge.example.com",
            ["FORGE_TOKEN"] = "t",
            ["FORGE_MCP_PORT"] = "9300",
        }, ForgeConfig.FromEnv);

        Assert.Equal(9300, cfg.Port);
    }

    // ── required inputs ─────────────────────────────────────────────────────────

    [Fact]
    public void Missing_base_url_throws_with_a_neutral_example()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WithEnv(new Dictionary<string, string?>
            {
                ["FORGE_BASE_URL"] = null,
                ["FORGE_TOKEN"] = "t",
            }, ForgeConfig.FromEnv));

        Assert.Contains("FORGE_BASE_URL", ex.Message);
        // The guidance example must stay forge-neutral — no real instance baked in.
        Assert.Contains("forge.example.com", ex.Message);
    }

    [Fact]
    public void Missing_token_throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WithEnv(new Dictionary<string, string?>
            {
                ["FORGE_BASE_URL"] = "https://forge.example.com",
                ["FORGE_TOKEN"] = null,
            }, ForgeConfig.FromEnv));

        Assert.Contains("FORGE_TOKEN", ex.Message);
    }
}
