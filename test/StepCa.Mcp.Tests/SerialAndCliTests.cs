using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace StepCa.Mcp.Tests;

/// <summary>
/// The serial-number decimal conversion and the <see cref="StepCli"/> subprocess
/// timeout/kill path.
/// </summary>
public sealed class SerialAndCliTests
{
    [Fact]
    public void SerialNumberAsDecimal_RoundTripsThroughInspect()
    {
        using var cert = CertFixtures.MakeCert();
        var dec = StepCaTools.SerialNumberAsDecimal(cert);

        // Independent recompute: hex serial → positive BigInteger → decimal.
        var hex = cert.SerialNumber;
        if (hex.Length % 2 != 0) hex = "0" + hex;
        var bytes = Convert.FromHexString(hex);
        Array.Reverse(bytes);
        var unsigned = new byte[bytes.Length + 1];
        Buffer.BlockCopy(bytes, 0, unsigned, 0, bytes.Length);
        var expected = new BigInteger(unsigned).ToString();

        Assert.Equal(expected, dec);
        Assert.True(BigInteger.Parse(dec) >= 0); // never negative
    }

    [Fact]
    public async Task StepCli_Timeout_KillsAndFlagsTimedOut()
    {
        // Use a real, ubiquitous sleeper to exercise the timeout/kill path.
        var sleep = File.Exists("/bin/sleep") ? "/bin/sleep"
            : File.Exists("/usr/bin/sleep") ? "/usr/bin/sleep"
            : throw new InvalidOperationException("no sleep binary on this host");

        var opts = new StepCaOptions(
            CaUrl: "https://ca.example.com:9000",
            CaRootCertPath: "/etc/step-ca-mcp/root_ca.crt",
            CaFingerprint: "",
            StepBin: sleep,
            IssuerProvisioner: "mcp-issuer",
            IssuerPasswordFile: "/etc/step-ca-mcp/mcp-issuer-password.txt",
            SubprocessTimeoutMs: 200);
        var cli = new StepCli(opts);

        var r = await cli.RunAsync(new[] { "10" }); // sleep 10s, but timeout is 200ms

        Assert.True(r.TimedOut);
        Assert.Equal(-1, r.ExitCode);
        Assert.Contains("killed after", r.Stderr);
    }
}
