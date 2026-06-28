using System.Text.Json.Nodes;
using Xunit;

namespace StepCa.Mcp.Tests;

/// <summary>
/// <see cref="StepCaTools.GetRootCertificate"/> reads the configured root cert
/// from disk and parses it; the missing-file path is intentionally asymmetric
/// (returns an <c>error</c> key without an <c>ok</c> key).
/// </summary>
public sealed class GetRootCertificateTests
{
    private static StepCaOptions OptsWithRoot(string path) => new(
        CaUrl: "https://ca.example.com:9000",
        CaRootCertPath: path,
        CaFingerprint: "",
        StepBin: "/usr/local/bin/step",
        IssuerProvisioner: "mcp-issuer",
        IssuerPasswordFile: "/etc/step-ca-mcp/mcp-issuer-password.txt",
        SubprocessTimeoutMs: 30_000);

    [Fact]
    public void MissingFile_ReturnsErrorWithoutOkKey()
    {
        var opts = OptsWithRoot(Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.crt"));

        var r = StepCaTools.GetRootCertificate(opts);

        Assert.Null(r["ok"]); // asymmetric: no ok key on this error path
        Assert.Contains("root cert not found", r["error"]!.GetValue<string>());
    }

    [Fact]
    public void PresentButMalformedFile_ReturnsErrorWithoutThrowing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bad-root-{Guid.NewGuid():N}.crt");
        File.WriteAllText(path, "this is not a certificate");
        try
        {
            var r = StepCaTools.GetRootCertificate(OptsWithRoot(path));

            Assert.Null(r["ok"]); // asymmetric error path, no ok key
            Assert.Contains("could not read root cert", r["error"]!.GetValue<string>());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PresentFile_ReturnsPemAndMetadata()
    {
        using var cert = CertFixtures.MakeCert(subjectCn: "Root CA Example");
        var pem = CertFixtures.ToPem(cert);
        var path = Path.Combine(Path.GetTempPath(), $"root-{Guid.NewGuid():N}.crt");
        File.WriteAllText(path, pem);
        try
        {
            var r = StepCaTools.GetRootCertificate(OptsWithRoot(path));

            Assert.Equal("pem", r["format"]!.GetValue<string>());
            Assert.Contains("Root CA Example", r["subject"]!.GetValue<string>());
            Assert.Equal(64, r["fingerprint_sha256"]!.GetValue<string>().Length);
            Assert.Contains("BEGIN CERTIFICATE", r["pem"]!.GetValue<string>());
            Assert.Matches("^[0-9]+$", r["serial_number"]!.GetValue<string>());
        }
        finally { File.Delete(path); }
    }
}
