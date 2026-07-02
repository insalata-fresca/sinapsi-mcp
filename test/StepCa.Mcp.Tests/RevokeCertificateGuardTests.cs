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

    // The free-text `reason` is validated alongside serial_number / reason_code,
    // and the guard short-circuits before any subprocess is spawned. A control
    // character or leading dash in the reason yields a structured error, not an
    // exception and not a `step` invocation.
    [Theory]
    [InlineData("bad\nreason")]   // embedded newline / control char
    [InlineData("-flagish")]      // leading dash (could be mistaken for a CLI flag)
    public async Task InvalidReason_ReturnsStructuredError(string reason)
    {
        var (cli, opts) = Harness();

        // A valid serial so the serial guard passes and the reason guard is reached.
        var r = await StepCaTools.RevokeCertificate(cli, opts, serial_number: "12345", reason: reason);

        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.False(string.IsNullOrWhiteSpace(r["error"]!.GetValue<string>()));
    }

    [Fact]
    public async Task TooLongReason_ReturnsStructuredError()
    {
        var (cli, opts) = Harness();
        var longReason = new string('x', StepCaValidation.MaxReasonLength + 1);

        var r = await StepCaTools.RevokeCertificate(cli, opts, serial_number: "12345", reason: longReason);

        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.Contains("too long", r["error"]!.GetValue<string>());
    }
}
