using Sinapsi.Forge;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// Fail-closed matrix for the shared HTTP-timeout binding used by BOTH forge hosts
/// (Forge.Mcp reads FORGE_HTTP_TIMEOUT_MS, Github.Mcp reads GITHUB_HTTP_TIMEOUT_MS —
/// both flow through <see cref="ForgeClientOptions.ReadHttpTimeoutMs"/>). An unset var
/// yields the default; a non-numeric, non-positive, or out-of-range value THROWS an
/// error naming the offending variable rather than silently running unbounded / broken.
/// </summary>
public sealed class ForgeClientOptionsTests
{
    private const string Var = "FORGE_TEST_TIMEOUT_MS";

    private static T WithVar<T>(string? value, Func<T> body)
    {
        var saved = Environment.GetEnvironmentVariable(Var);
        try
        {
            Environment.SetEnvironmentVariable(Var, value);
            return body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(Var, saved);
        }
    }

    [Fact]
    public void Unset_yields_the_default()
        => Assert.Equal(ForgeClientOptions.DefaultHttpTimeoutMs, WithVar(null, () => ForgeClientOptions.ReadHttpTimeoutMs(Var)));

    [Fact]
    public void Empty_yields_the_default()
        => Assert.Equal(ForgeClientOptions.DefaultHttpTimeoutMs, WithVar("   ", () => ForgeClientOptions.ReadHttpTimeoutMs(Var)));

    [Fact]
    public void Valid_value_is_honoured()
        => Assert.Equal(15_000, WithVar("15000", () => ForgeClientOptions.ReadHttpTimeoutMs(Var)));

    [Theory]
    [InlineData("0")]          // instant-timeout footgun
    [InlineData("-1")]         // negative
    [InlineData("notanumber")] // non-numeric
    [InlineData("999999999")]  // above the 600000 ms ceiling
    public void Invalid_value_throws_naming_the_variable(string bad)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => WithVar(bad, () => ForgeClientOptions.ReadHttpTimeoutMs(Var)));
        Assert.Contains(Var, ex.Message);
    }

    [Fact]
    public void Ceiling_value_is_accepted()
        => Assert.Equal(ForgeClientOptions.MaxHttpTimeoutMs,
            WithVar(ForgeClientOptions.MaxHttpTimeoutMs.ToString(), () => ForgeClientOptions.ReadHttpTimeoutMs(Var)));
}
