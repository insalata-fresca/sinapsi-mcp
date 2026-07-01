// ---------------------------------------------------------------------------
// GeminiConfigTests — env binding + the FAIL-CLOSED timeout clamp. Mirrors the
// StepCa.Mcp exemplar (StepCaOptionsTests): neutral defaults when nothing is set,
// env overrides winning, and a set-but-invalid timeout (non-numeric / <= 0 /
// out-of-range) THROWING an error that names the offending env var (fail startup)
// rather than silently falling back to a default footgun.
// ---------------------------------------------------------------------------
using Gemini.Mcp;
using Xunit;

namespace Gemini.Mcp.Tests;

/// <summary>
/// Pins <see cref="GeminiConfig.FromEnvironment"/>: the generic defaults, env
/// overrides winning, the research timeout being independent of the interactive
/// one, and — the hardening leg — a set-but-invalid timeout failing closed by
/// throwing an error naming the var, instead of being swallowed into the default.
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
    public void Defaults_are_neutral_and_not_site_specific()
    {
        var cfg = WithEnv(new Dictionary<string, string?>(), GeminiConfig.FromEnvironment);

        // No homelab provenance / product names / internal hostnames in the defaults.
        foreach (var value in new[] { cfg.OutputDir, cfg.SessionDir, cfg.TaskDir, cfg.GeminiBin })
        {
            Assert.DoesNotContain("10.42", value);
            Assert.DoesNotContain("ct1", value.ToLowerInvariant());
            Assert.DoesNotContain("mcp-gateway", value);
        }
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

    // ── fail-closed timeout clamp ────────────────────────────────────────────
    // A set-but-invalid timeout is a config error, not a value to swallow: 0 would
    // make every subprocess time out instantly and a negative value throws deep in
    // the CancellationTokenSource ctor. Both — and a non-numeric value and an
    // over-ceiling value — are rejected with an error naming the offending var.

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("12.5")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("-30000")]
    public void Invalid_interactive_timeout_is_rejected_naming_the_var(string bad)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WithEnv(new Dictionary<string, string?> { ["GEMINI_TIMEOUT_MS"] = bad },
                GeminiConfig.FromEnvironment));
        Assert.Contains("GEMINI_TIMEOUT_MS", ex.Message);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-1")]
    public void Invalid_research_timeout_is_rejected_naming_the_var(string bad)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WithEnv(new Dictionary<string, string?> { ["GEMINI_RESEARCH_TIMEOUT_MS"] = bad },
                GeminiConfig.FromEnvironment));
        Assert.Contains("GEMINI_RESEARCH_TIMEOUT_MS", ex.Message);
    }

    [Fact]
    public void Absurdly_large_timeout_is_rejected()
    {
        var over = (GeminiConfig.MaxTimeoutMs + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WithEnv(new Dictionary<string, string?> { ["GEMINI_TIMEOUT_MS"] = over },
                GeminiConfig.FromEnvironment));
        Assert.Contains("GEMINI_TIMEOUT_MS", ex.Message);
    }

    [Fact]
    public void Timeout_at_the_ceiling_is_accepted()
    {
        var cfg = WithEnv(new Dictionary<string, string?>
        {
            ["GEMINI_TIMEOUT_MS"] = GeminiConfig.MaxTimeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }, GeminiConfig.FromEnvironment);
        Assert.Equal(GeminiConfig.MaxTimeoutMs, cfg.DefaultTimeoutMs);
    }

    [Fact]
    public void Empty_timeout_var_uses_the_default_rather_than_throwing()
    {
        // An unset/empty var is not a config error — it simply selects the default.
        var cfg = WithEnv(new Dictionary<string, string?>
        {
            ["GEMINI_TIMEOUT_MS"] = "",
            ["GEMINI_RESEARCH_TIMEOUT_MS"] = "",
        }, GeminiConfig.FromEnvironment);

        Assert.Equal(180_000, cfg.DefaultTimeoutMs);
        Assert.Equal(1_800_000, cfg.ResearchTimeoutMs);
    }
}
