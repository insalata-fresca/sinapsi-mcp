using Metabase.Mcp;
using Xunit;

namespace Metabase.Mcp.Tests;

// -----------------------------------------------------------------------------
// Config fail-closed matrix: the port and HTTP-timeout binding must THROW naming
// the offending env var on a non-numeric / out-of-range value rather than
// silently falling back to a default (which would mask a typo and bind/behave
// somewhere unintended). Mirrors StepCaOptionsTests / ZitadelConfigTests.
// -----------------------------------------------------------------------------

/// <summary>
/// Fail-closed configuration tests for <see cref="MetabaseConfig"/>. Beyond the happy-path
/// binding already covered by <see cref="MetabaseConfigTests"/>, these pin that an invalid
/// <c>METABASE_MCP_PORT</c> or <c>METABASE_HTTP_TIMEOUT_MS</c> is REJECTED at startup with a
/// clear error naming the var, and that a valid timeout binds + clamps at the documented
/// ceiling.
/// </summary>
// Shares the "metabase-env" xUnit collection with MetabaseConfigTests so the two classes never
// run in parallel — both mutate the same process-global METABASE_* env vars. Without this,
// MetabaseConfigTests nulling METABASE_API_KEY concurrently tripped the API-key throw before the
// timeout check these tests assert on (intermittent failure). Mirrors StepCa's [Collection].
[Collection("metabase-env")]
public sealed class MetabaseConfigFailClosedTests
{
    private static T WithEnv<T>(IReadOnlyDictionary<string, string?> env, Func<T> body)
    {
        var keys = new[] { "METABASE_BASE_URL", "METABASE_API_KEY", "METABASE_MCP_PORT", "METABASE_HTTP_TIMEOUT_MS" };
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

    private static Dictionary<string, string?> Base(params (string, string?)[] extra)
    {
        var d = new Dictionary<string, string?>
        {
            ["METABASE_BASE_URL"] = "https://metrics.example.com",
            ["METABASE_API_KEY"] = "mb-key",
        };
        foreach (var (k, v) in extra) d[k] = v;
        return d;
    }

    // ── HTTP timeout: default, override, and fail-closed matrix ─────────────────

    [Fact]
    public void Http_timeout_defaults_when_unset()
    {
        var cfg = WithEnv(Base(), MetabaseConfig.FromEnv);
        Assert.Equal(MetabaseConfig.DefaultHttpTimeoutMs, cfg.HttpTimeoutMs);
    }

    [Fact]
    public void Http_timeout_binds_a_valid_override()
    {
        var cfg = WithEnv(Base(("METABASE_HTTP_TIMEOUT_MS", "45000")), MetabaseConfig.FromEnv);
        Assert.Equal(45000, cfg.HttpTimeoutMs);
    }

    [Fact]
    public void Http_timeout_accepts_the_ceiling()
    {
        var cfg = WithEnv(Base(("METABASE_HTTP_TIMEOUT_MS", MetabaseConfig.MaxHttpTimeoutMs.ToString())), MetabaseConfig.FromEnv);
        Assert.Equal(MetabaseConfig.MaxHttpTimeoutMs, cfg.HttpTimeoutMs);
    }

    [Theory]
    [InlineData("0")]            // instant-timeout footgun
    [InlineData("-1")]           // throws inside HttpClient.Timeout setter
    [InlineData("not-a-number")] // non-numeric
    [InlineData("600001")]       // one past the ceiling
    [InlineData("99999999999")]  // overflows int
    public void Http_timeout_invalid_value_fails_startup_naming_the_var(string raw)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WithEnv(Base(("METABASE_HTTP_TIMEOUT_MS", raw)), MetabaseConfig.FromEnv));
        Assert.Contains("METABASE_HTTP_TIMEOUT_MS", ex.Message);
        Assert.Contains(raw, ex.Message);
    }

    // ── Port: fail-closed matrix ────────────────────────────────────────────────

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("70000")]        // above 65535
    [InlineData("not-a-port")]
    public void Port_invalid_value_fails_startup_naming_the_var(string raw)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WithEnv(Base(("METABASE_MCP_PORT", raw)), MetabaseConfig.FromEnv));
        Assert.Contains("METABASE_MCP_PORT", ex.Message);
        Assert.Contains(raw, ex.Message);
    }

    [Fact]
    public void Port_binds_a_valid_override()
    {
        var cfg = WithEnv(Base(("METABASE_MCP_PORT", "9444")), MetabaseConfig.FromEnv);
        Assert.Equal(9444, cfg.Port);
    }
}
