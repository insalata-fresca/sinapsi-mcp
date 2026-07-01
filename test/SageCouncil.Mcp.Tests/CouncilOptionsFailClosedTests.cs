using Xunit;

namespace SageCouncil.Mcp.Tests;

// -----------------------------------------------------------------------------
// Config fail-closed matrix for the two timeout env vars. Mirrors the
// reference-grade StepCaOptionsTests: a non-numeric / non-positive / above-ceiling
// value must FAIL STARTUP with an InvalidOperationException naming the offending
// env var, rather than being silently swallowed into a footgun default.
// -----------------------------------------------------------------------------

/// <summary>
/// Exercises <see cref="CouncilOptions.ReadTimeoutMs"/> via
/// <see cref="CouncilOptions.FromEnvironment"/>. These run serially (the "env"
/// collection) so concurrent env mutation cannot cross-talk.
/// </summary>
[Collection("env")]
public sealed class CouncilOptionsFailClosedTests
{
    private static IDisposable Env(params (string Key, string? Value)[] vars)
    {
        var prior = vars.Select(v => (v.Key, Old: Environment.GetEnvironmentVariable(v.Key))).ToArray();
        foreach (var (k, v) in vars) Environment.SetEnvironmentVariable(k, v);
        return new Restore(prior);
    }

    private sealed class Restore((string Key, string? Old)[] prior) : IDisposable
    {
        public void Dispose()
        {
            foreach (var (k, old) in prior) Environment.SetEnvironmentVariable(k, old);
        }
    }

    [Fact]
    public void Unset_timeouts_fall_back_to_the_neutral_defaults()
    {
        using var _ = Env(
            ("SAGE_TIMEOUT_MS", null), ("SAGE_MEMBER_TIMEOUT_MS", null),
            ("PERSONA_DIR", "/nonexistent-persona-dir"));

        var opt = CouncilOptions.FromEnvironment();

        Assert.Equal(CouncilOptions.DefaultTimeoutMs, (int)opt.Timeout.TotalMilliseconds);
        Assert.Equal(CouncilOptions.DefaultMemberTimeoutMs, (int)opt.MemberDeadline.TotalMilliseconds);
    }

    [Fact]
    public void A_valid_in_range_timeout_is_honoured()
    {
        using var _ = Env(
            ("SAGE_TIMEOUT_MS", "60000"), ("SAGE_MEMBER_TIMEOUT_MS", "30000"),
            ("PERSONA_DIR", "/nonexistent-persona-dir"));

        var opt = CouncilOptions.FromEnvironment();

        Assert.Equal(60000, (int)opt.Timeout.TotalMilliseconds);
        Assert.Equal(30000, (int)opt.MemberDeadline.TotalMilliseconds);
    }

    [Theory]
    [InlineData("SAGE_TIMEOUT_MS", "not-a-number")]
    [InlineData("SAGE_TIMEOUT_MS", "0")]
    [InlineData("SAGE_TIMEOUT_MS", "-1")]
    [InlineData("SAGE_TIMEOUT_MS", "7200001")]        // one past the 2h ceiling
    [InlineData("SAGE_MEMBER_TIMEOUT_MS", "not-a-number")]
    [InlineData("SAGE_MEMBER_TIMEOUT_MS", "0")]
    [InlineData("SAGE_MEMBER_TIMEOUT_MS", "-5")]
    [InlineData("SAGE_MEMBER_TIMEOUT_MS", "99999999")] // well past the ceiling
    public void An_invalid_timeout_fails_startup_naming_the_offending_var(string var, string value)
    {
        using var _ = Env((var, value), ("PERSONA_DIR", "/nonexistent-persona-dir"));

        var ex = Assert.Throws<InvalidOperationException>(() => CouncilOptions.FromEnvironment());
        // The error must name the offending env var so an operator can find it.
        Assert.Contains(var, ex.Message);
    }

    [Fact]
    public void The_ceiling_boundary_value_is_accepted()
    {
        using var _ = Env(
            ("SAGE_TIMEOUT_MS", CouncilOptions.MaxTimeoutMs.ToString()),
            ("PERSONA_DIR", "/nonexistent-persona-dir"));

        var opt = CouncilOptions.FromEnvironment();

        Assert.Equal(CouncilOptions.MaxTimeoutMs, (int)opt.Timeout.TotalMilliseconds);
    }
}
