using Sinapsi.Forge;
using Xunit;

namespace Forge.Mcp.Tests;

/// <summary>
/// Fail-closed matrix for the Forge.Mcp host's canonical HTTP-timeout env var,
/// <c>FORGE_HTTP_TIMEOUT_MS</c>. Unset → default; a non-numeric / non-positive /
/// out-of-range value fails startup with an error naming the variable, rather than
/// binding an unbounded or instantly-timing-out HttpClient.
/// </summary>
public sealed class ForgeHttpTimeoutConfigTests
{
    private const string Var = "FORGE_HTTP_TIMEOUT_MS";

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
    public void Unset_uses_the_default()
        => Assert.Equal(ForgeClientOptions.DefaultHttpTimeoutMs,
            WithVar(null, () => ForgeClientOptions.ReadHttpTimeoutMs(Var)));

    [Fact]
    public void Valid_value_is_honoured()
        => Assert.Equal(20_000, WithVar("20000", () => ForgeClientOptions.ReadHttpTimeoutMs(Var)));

    [Theory]
    [InlineData("0")]
    [InlineData("-100")]
    [InlineData("abc")]
    [InlineData("600001")]   // one past the ceiling
    public void Invalid_value_fails_closed_naming_the_var(string bad)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => WithVar(bad, () => ForgeClientOptions.ReadHttpTimeoutMs(Var)));
        Assert.Contains(Var, ex.Message);
    }
}
