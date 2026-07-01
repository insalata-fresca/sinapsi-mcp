using Xunit;

namespace Gdrive.Mcp.Tests;

/// <summary>
/// Fail-closed configuration matrix for the Drive <c>HttpClient</c> timeout
/// ceiling (<c>GDRIVE_MCP_HTTP_TIMEOUT_SECONDS</c>). Mirrors the StepCa.Mcp
/// options-hardening suite: a sane default, a hard ceiling, and a THROW that
/// names the offending env var on any non-numeric / non-positive / out-of-range
/// value — the server refuses to start on a footgun rather than honouring it.
///
/// Shares the same non-parallel "env" collection as <see cref="GdriveConfigTests"/>
/// because these mutate process environment variables.
/// </summary>
[Collection("env")]
public sealed class GdriveConfigHardeningTests : IDisposable
{
    private const string Var = "GDRIVE_MCP_HTTP_TIMEOUT_SECONDS";

    public GdriveConfigHardeningTests() => Clear();
    public void Dispose() => Clear();
    private static void Clear() => Environment.SetEnvironmentVariable(Var, null);

    [Fact]
    public void Default_WhenUnset_IsTheDocumentedDefault()
    {
        Clear();
        Assert.Equal(GdriveConfig.DefaultHttpTimeoutSeconds, GdriveConfig.FromEnvironment().HttpTimeoutSeconds);
    }

    [Fact]
    public void ValidValue_IsBound()
    {
        Environment.SetEnvironmentVariable(Var, "45");
        Assert.Equal(45, GdriveConfig.FromEnvironment().HttpTimeoutSeconds);
    }

    [Fact]
    public void CeilingValue_IsAccepted()
    {
        Environment.SetEnvironmentVariable(
            Var, GdriveConfig.MaxHttpTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(GdriveConfig.MaxHttpTimeoutSeconds, GdriveConfig.FromEnvironment().HttpTimeoutSeconds);
    }

    // 0 would make every request time out instantly; a negative value throws
    // inside HttpClient.Timeout. Both are rejected as invalid config with a clear
    // error naming the offending env var, rather than being silently honoured.
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("-3600")]
    [InlineData("not-a-number")]
    [InlineData("12.5")]
    public void BadValue_IsRejected_NamingTheVar(string bad)
    {
        Environment.SetEnvironmentVariable(Var, bad);
        var ex = Assert.Throws<InvalidOperationException>(() => GdriveConfig.FromEnvironment());
        Assert.Contains(Var, ex.Message);
    }

    [Fact]
    public void AbsurdlyLargeValue_IsRejected()
    {
        Environment.SetEnvironmentVariable(
            Var, (GdriveConfig.MaxHttpTimeoutSeconds + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var ex = Assert.Throws<InvalidOperationException>(() => GdriveConfig.FromEnvironment());
        Assert.Contains(Var, ex.Message);
    }
}
