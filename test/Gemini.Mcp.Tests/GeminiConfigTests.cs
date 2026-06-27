using Gemini.Mcp;
using Xunit;

namespace Gemini.Mcp.Tests;

/// <summary>
/// Every path and timeout the server uses is resolved from the environment so it can be
/// pointed at any layout without a rebuild. These tests pin that resolution: the generic
/// defaults when nothing is set, env overrides winning, and a non-numeric timeout falling
/// back to the default rather than throwing.
/// </summary>
public sealed class GeminiConfigTests
{
    private static readonly string[] Keys =
    {
        "NANO_BANANA_OUTPUT_DIR", "GEMINI_SESSION_DIR", "GEMINI_TASK_DIR",
        "GEMINI_BIN", "GEMINI_TIMEOUT_MS", "GEMINI_RESEARCH_TIMEOUT_MS",
    };

    private static T WithEnv<T>(IReadOnlyDictionary<string, string?> env, Func<T> body)
    {
        var saved = Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var k in Keys) Environment.SetEnvironmentVariable(k, env.TryGetValue(k, out var v) ? v : null);
            return body();
        }
        finally
        {
            foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v);
        }
    }

    [Fact]
    public void Defaults_apply_when_no_env_is_set()
    {
        var cfg = WithEnv(new Dictionary<string, string?>(), GeminiConfig.FromEnvironment);

        Assert.Equal("/var/lib/nano-banana/output", cfg.OutputDir);
        Assert.Equal("/var/lib/gemini-mcp/sessions", cfg.SessionDir);
        Assert.Equal("/var/lib/gemini-mcp/tasks", cfg.TaskDir);
        Assert.EndsWith("gemini.js", cfg.GeminiBin);
        Assert.Equal(180_000, cfg.DefaultTimeoutMs);
        Assert.Equal(1_800_000, cfg.ResearchTimeoutMs);
    }

    [Fact]
    public void Env_overrides_win_for_every_field()
    {
        var cfg = WithEnv(new Dictionary<string, string?>
        {
            ["NANO_BANANA_OUTPUT_DIR"] = "/tmp/out",
            ["GEMINI_SESSION_DIR"] = "/tmp/sess",
            ["GEMINI_TASK_DIR"] = "/tmp/tasks",
            ["GEMINI_BIN"] = "/opt/gemini/bundle.js",
            ["GEMINI_TIMEOUT_MS"] = "5000",
            ["GEMINI_RESEARCH_TIMEOUT_MS"] = "60000",
        }, GeminiConfig.FromEnvironment);

        Assert.Equal("/tmp/out", cfg.OutputDir);
        Assert.Equal("/tmp/sess", cfg.SessionDir);
        Assert.Equal("/tmp/tasks", cfg.TaskDir);
        Assert.Equal("/opt/gemini/bundle.js", cfg.GeminiBin);
        Assert.Equal(5_000, cfg.DefaultTimeoutMs);
        Assert.Equal(60_000, cfg.ResearchTimeoutMs);
    }

    [Fact]
    public void Research_timeout_is_independent_of_the_interactive_timeout()
    {
        // The whole point of the separate budget: a deep research run is not capped by
        // the shorter interactive timeout.
        var cfg = WithEnv(new Dictionary<string, string?>
        {
            ["GEMINI_TIMEOUT_MS"] = "1000",
        }, GeminiConfig.FromEnvironment);

        Assert.Equal(1_000, cfg.DefaultTimeoutMs);
        Assert.Equal(1_800_000, cfg.ResearchTimeoutMs);
        Assert.True(cfg.ResearchTimeoutMs > cfg.DefaultTimeoutMs);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("")]
    [InlineData("12.5")]
    public void Non_integer_timeout_falls_back_to_the_default(string raw)
    {
        var cfg = WithEnv(new Dictionary<string, string?>
        {
            ["GEMINI_TIMEOUT_MS"] = raw,
            ["GEMINI_RESEARCH_TIMEOUT_MS"] = raw,
        }, GeminiConfig.FromEnvironment);

        Assert.Equal(180_000, cfg.DefaultTimeoutMs);
        Assert.Equal(1_800_000, cfg.ResearchTimeoutMs);
    }
}
