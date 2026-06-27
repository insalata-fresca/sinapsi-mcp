using Xunit;

namespace StepCa.Mcp.Tests;

/// <summary>
/// The empty-serial guard on <see cref="StepCaTools.RevokeCertificate"/> short-
/// circuits before any subprocess is spawned, so it is exercisable with a CLI
/// that points at a binary that never runs.
/// </summary>
public sealed class RevokeCertificateGuardTests
{
    private static (StepCli cli, StepCaOptions opts) Harness()
    {
        var opts = new StepCaOptions(
            CaUrl: "https://ca.example.com:9000",
            CaRootCertPath: "/etc/step-ca-mcp/root_ca.crt",
            CaFingerprint: "",
            StepBin: "/nonexistent/step", // never invoked on the guard path
            IssuerProvisioner: "mcp-issuer",
            IssuerPasswordFile: "/etc/step-ca-mcp/mcp-issuer-password.txt",
            SubprocessTimeoutMs: 30_000);
        return (new StepCli(opts), opts);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyOrWhitespaceSerial_ReturnsRequiredError(string serial)
    {
        var (cli, opts) = Harness();

        var r = await StepCaTools.RevokeCertificate(cli, opts, serial);

        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.Equal("serial_number is required", r["error"]!.GetValue<string>());
    }
}
