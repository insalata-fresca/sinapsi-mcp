using Xunit;

namespace StepCa.Mcp.Tests;

/// <summary>
/// <see cref="StepCaOptions.FromEnvironment"/>: STEP_CA_URL is required; every
/// other value has a neutral default; the timeout has a canonical var plus a
/// back-compat alias. These mutate process env, so they run serially.
/// </summary>
[Collection("stepca-env")]
public sealed class StepCaOptionsTests : IDisposable
{
    private static readonly string[] Vars =
    {
        "STEP_CA_URL", "STEP_CA_ROOT_CERT", "STEP_BIN",
        "MCP_ISSUER_PROVISIONER", "MCP_ISSUER_PASSWORD_FILE",
        "STEP_SUBPROCESS_TIMEOUT_MS", "STEP_CA_HTTP_TIMEOUT_MS",
    };

    public StepCaOptionsTests() => ClearAll();
    public void Dispose() => ClearAll();
    private static void ClearAll() { foreach (var v in Vars) Environment.SetEnvironmentVariable(v, null); }

    [Fact]
    public void MissingCaUrl_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => StepCaOptions.FromEnvironment());
        Assert.Contains("STEP_CA_URL", ex.Message);
    }

    [Fact]
    public void DefaultsAreNeutral_AndNotSiteSpecific()
    {
        Environment.SetEnvironmentVariable("STEP_CA_URL", "https://ca.example.com:9000");

        var o = StepCaOptions.FromEnvironment();

        Assert.Equal("https://ca.example.com:9000", o.CaUrl);
        Assert.Equal("/etc/step-ca-mcp/root_ca.crt", o.CaRootCertPath);
        Assert.Equal("/usr/local/bin/step", o.StepBin);
        Assert.Equal("mcp-issuer", o.IssuerProvisioner);
        Assert.Equal("/etc/step-ca-mcp/mcp-issuer-password.txt", o.IssuerPasswordFile);
        Assert.Equal(30_000, o.SubprocessTimeoutMs);
    }

    [Fact]
    public void EveryVar_IsBound()
    {
        Environment.SetEnvironmentVariable("STEP_CA_URL", "https://other.example.org:9100");
        Environment.SetEnvironmentVariable("STEP_CA_ROOT_CERT", "/secrets/root.pem");
        Environment.SetEnvironmentVariable("STEP_BIN", "/opt/step/step");
        Environment.SetEnvironmentVariable("MCP_ISSUER_PROVISIONER", "issuer-x");
        Environment.SetEnvironmentVariable("MCP_ISSUER_PASSWORD_FILE", "/secrets/pw.txt");
        Environment.SetEnvironmentVariable("STEP_SUBPROCESS_TIMEOUT_MS", "12345");

        var o = StepCaOptions.FromEnvironment();

        Assert.Equal("https://other.example.org:9100", o.CaUrl);
        Assert.Equal("/secrets/root.pem", o.CaRootCertPath);
        Assert.Equal("/opt/step/step", o.StepBin);
        Assert.Equal("issuer-x", o.IssuerProvisioner);
        Assert.Equal("/secrets/pw.txt", o.IssuerPasswordFile);
        Assert.Equal(12345, o.SubprocessTimeoutMs);
    }

    [Fact]
    public void TimeoutAlias_UsedOnlyWhenCanonicalUnset()
    {
        Environment.SetEnvironmentVariable("STEP_CA_URL", "https://ca.example.com:9000");
        Environment.SetEnvironmentVariable("STEP_CA_HTTP_TIMEOUT_MS", "7777");

        Assert.Equal(7777, StepCaOptions.FromEnvironment().SubprocessTimeoutMs);
    }

    [Fact]
    public void CanonicalTimeout_WinsOverAlias()
    {
        Environment.SetEnvironmentVariable("STEP_CA_URL", "https://ca.example.com:9000");
        Environment.SetEnvironmentVariable("STEP_SUBPROCESS_TIMEOUT_MS", "5000");
        Environment.SetEnvironmentVariable("STEP_CA_HTTP_TIMEOUT_MS", "9999");

        Assert.Equal(5000, StepCaOptions.FromEnvironment().SubprocessTimeoutMs);
    }

    // ── timeout clamp (fail-closed on bad config) ────────────────────────────
    // 0 would make every subprocess time out instantly; a negative value throws
    // inside the CancellationTokenSource ctor. Both are rejected as invalid
    // config with a clear error naming the offending env var, rather than being
    // silently honoured.
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("-30000")]
    public void NonPositiveTimeout_IsRejected_NamingTheVar(string bad)
    {
        Environment.SetEnvironmentVariable("STEP_CA_URL", "https://ca.example.com:9000");
        Environment.SetEnvironmentVariable("STEP_SUBPROCESS_TIMEOUT_MS", bad);

        var ex = Assert.Throws<InvalidOperationException>(() => StepCaOptions.FromEnvironment());
        Assert.Contains("STEP_SUBPROCESS_TIMEOUT_MS", ex.Message);
    }

    [Fact]
    public void AbsurdlyLargeTimeout_IsRejected()
    {
        Environment.SetEnvironmentVariable("STEP_CA_URL", "https://ca.example.com:9000");
        Environment.SetEnvironmentVariable(
            "STEP_SUBPROCESS_TIMEOUT_MS",
            (StepCaOptions.MaxSubprocessTimeoutMs + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.Throws<InvalidOperationException>(() => StepCaOptions.FromEnvironment());
    }

    [Fact]
    public void NonPositiveTimeout_OnAlias_IsAlsoRejected_NamingTheAlias()
    {
        Environment.SetEnvironmentVariable("STEP_CA_URL", "https://ca.example.com:9000");
        Environment.SetEnvironmentVariable("STEP_CA_HTTP_TIMEOUT_MS", "0");

        var ex = Assert.Throws<InvalidOperationException>(() => StepCaOptions.FromEnvironment());
        Assert.Contains("STEP_CA_HTTP_TIMEOUT_MS", ex.Message);
    }
}
