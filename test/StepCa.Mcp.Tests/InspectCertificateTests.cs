using System.Text.Json.Nodes;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace StepCa.Mcp.Tests;

/// <summary>
/// <see cref="StepCaTools.InspectCertificate"/> is pure (no subprocess): it
/// parses a supplied PEM with the BCL and returns metadata. These tests cover
/// the happy path, the DNS+IP SAN enumeration, the expiry computation, the
/// 64 KiB size cap and the parse-error path.
/// </summary>
public sealed class InspectCertificateTests
{
    [Fact]
    public void ValidCert_ReturnsOkWithCoreFields()
    {
        using var cert = CertFixtures.MakeCert(subjectCn: "node.example.com");
        var pem = CertFixtures.ToPem(cert);

        var r = StepCaTools.InspectCertificate(pem);

        Assert.True(r["ok"]!.GetValue<bool>());
        Assert.Contains("node.example.com", r["subject"]!.GetValue<string>());
        Assert.Contains("node.example.com", r["issuer"]!.GetValue<string>()); // self-signed
        Assert.False(r["expired"]!.GetValue<bool>());
        // 64 lowercase hex chars for a SHA-256 fingerprint.
        var fp = r["fingerprint_sha256"]!.GetValue<string>();
        Assert.Equal(64, fp.Length);
        Assert.Equal(fp.ToLowerInvariant(), fp);
        // Serial is a non-empty decimal string.
        Assert.Matches("^[0-9]+$", r["serial_number"]!.GetValue<string>());
    }

    [Fact]
    public void DnsAndIpSans_AreEnumerated()
    {
        using var cert = CertFixtures.MakeCert(
            subjectCn: "svc.example.com",
            dnsSans: new[] { "a.example.com", "b.example.com" },
            ipSans: new[] { "10.0.0.4" });
        var pem = CertFixtures.ToPem(cert);

        var r = StepCaTools.InspectCertificate(pem);
        var sans = (JsonArray)r["subject_alt_names"]!;
        var values = sans.Select(n => n!.GetValue<string>()).ToList();

        Assert.Contains("a.example.com", values);
        Assert.Contains("b.example.com", values);
        Assert.Contains("10.0.0.4", values);
    }

    [Fact]
    public void ExpiredCert_FlagsExpiredAndNegativeRemaining()
    {
        using var cert = CertFixtures.MakeCert(
            subjectCn: "old.example.com",
            notBefore: DateTimeOffset.UtcNow.AddDays(-10),
            notAfter: DateTimeOffset.UtcNow.AddDays(-1));
        var pem = CertFixtures.ToPem(cert);

        var r = StepCaTools.InspectCertificate(pem);

        Assert.True(r["ok"]!.GetValue<bool>());
        Assert.True(r["expired"]!.GetValue<bool>());
        Assert.True(r["seconds_until_expiry"]!.GetValue<long>() < 0);
    }

    [Fact]
    public void OversizePem_IsRefusedBeforeParsing()
    {
        var huge = new string('A', 65_537);

        var r = StepCaTools.InspectCertificate(huge);

        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.Contains("too large", r["error"]!.GetValue<string>());
    }

    [Fact]
    public void GarbagePem_ReturnsParseError()
    {
        var r = StepCaTools.InspectCertificate("-----BEGIN CERTIFICATE-----\nnotbase64\n-----END CERTIFICATE-----");

        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.Contains("could not parse", r["error"]!.GetValue<string>());
    }

    [Fact]
    public void NotBeforeNotAfter_AreRoundTripUtcStrings()
    {
        using var cert = CertFixtures.MakeCert();
        var pem = CertFixtures.ToPem(cert);

        var r = StepCaTools.InspectCertificate(pem);

        // "O" round-trip format ends in Z (UTC) and parses back cleanly.
        var nb = r["not_before"]!.GetValue<string>();
        var na = r["not_after"]!.GetValue<string>();
        Assert.EndsWith("Z", nb);
        Assert.EndsWith("Z", na);
        Assert.True(DateTimeOffset.Parse(nb) < DateTimeOffset.Parse(na));
    }
}
