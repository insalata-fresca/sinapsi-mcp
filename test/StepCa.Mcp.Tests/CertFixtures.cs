using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace StepCa.Mcp.Tests;

/// <summary>
/// Helpers to mint self-signed test certificates (with controllable validity,
/// subject and SANs) so the BCL-parsing tools can be exercised without a live CA.
/// </summary>
internal static class CertFixtures
{
    internal static X509Certificate2 MakeCert(
        string subjectCn = "test.example.com",
        string[]? dnsSans = null,
        string[]? ipSans = null,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            $"CN={subjectCn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        if ((dnsSans is { Length: > 0 }) || (ipSans is { Length: > 0 }))
        {
            var san = new SubjectAlternativeNameBuilder();
            foreach (var d in dnsSans ?? Array.Empty<string>()) san.AddDnsName(d);
            foreach (var ip in ipSans ?? Array.Empty<string>())
                san.AddIpAddress(System.Net.IPAddress.Parse(ip));
            req.CertificateExtensions.Add(san.Build());
        }

        var nb = notBefore ?? DateTimeOffset.UtcNow.AddMinutes(-5);
        var na = notAfter ?? DateTimeOffset.UtcNow.AddDays(365);
        return req.CreateSelfSigned(nb, na);
    }

    internal static string ToPem(X509Certificate2 cert) =>
        new string(PemEncoding.Write("CERTIFICATE", cert.RawData));
}
