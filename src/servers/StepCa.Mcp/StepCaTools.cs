using System.ComponentModel;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace StepCa.Mcp;

/// <summary>
/// The step-ca tool surface (6 tools). Subprocess calls go through
/// <see cref="StepCli"/> (H3/H5 hardened); certificate parsing uses the .NET
/// BCL (System.Security.Cryptography.X509Certificates).
/// </summary>
[McpServerToolType]
public sealed class StepCaTools
{
    // ── 1. get_ca_health ────────────────────────────────────────────────────
    [McpServerTool(Name = "get_ca_health")]
    [Description("Check whether the step-ca server is healthy and reachable.")]
    public static async Task<JsonObject> GetCaHealth(StepCli step, StepCaOptions opts, CancellationToken ct)
    {
        var h = await step.RunAsync(["ca", "health", "--ca-url", opts.CaUrl, "--root", opts.CaRootCertPath], ct);
        var v = await step.RunAsync(["version"], ct);
        var rootPresent = File.Exists(opts.CaRootCertPath);
        return new JsonObject
        {
            // Exact-match on trimmed stdout. `step ca health` outputs literally
            // "ok\n" on success; a substring Contains() can false-positive on any
            // stdout-leaked error text containing the letters "ok".
            ["ok"] = h.ExitCode == 0 && h.Stdout.Trim().Equals("ok", StringComparison.OrdinalIgnoreCase),
            ["ca_url"] = opts.CaUrl,
            ["step_health_output"] = h.Stdout.Trim(),
            ["step_health_error"] = string.IsNullOrWhiteSpace(h.Stderr) ? null : h.Stderr.Trim(),
            ["step_cli_version"] = v.ExitCode == 0 ? v.Stdout.Trim().Split('\n').FirstOrDefault() : null,
            ["root_certificate_path"] = opts.CaRootCertPath,
            ["root_certificate_present"] = rootPresent,
        };
    }

    // ── 2. get_root_certificate ─────────────────────────────────────────────
    [McpServerTool(Name = "get_root_certificate")]
    [Description("Return the internal CA root certificate in PEM format.")]
    public static JsonObject GetRootCertificate(StepCaOptions opts)
    {
        if (!File.Exists(opts.CaRootCertPath))
        {
            // Error path intentionally omits the "ok" key (kept asymmetric).
            return new JsonObject { ["error"] = $"root cert not found at {opts.CaRootCertPath}" };
        }

        string pem;
        X509Certificate2 cert;
        try
        {
            pem = File.ReadAllText(opts.CaRootCertPath);
            // Dispose the X509Certificate2 (unmanaged crypto handle).
            cert = X509Certificate2.CreateFromPem(pem);
        }
        catch (Exception e)
        {
            // A present-but-unreadable / malformed root cert must surface as a
            // structured error, not bubble as an unhandled exception. The path is
            // operator-controlled config, so echoing it is safe; the BCL message
            // never contains key material for a public cert.
            return new JsonObject { ["error"] = $"could not read root cert at {opts.CaRootCertPath}: {e.Message}" };
        }

        using (cert)
        return new JsonObject
        {
            ["format"] = "pem",
            ["subject"] = cert.SubjectName.Name,
            ["issuer"] = cert.IssuerName.Name,
            ["serial_number"] = SerialNumberAsDecimal(cert),
            ["fingerprint_sha256"] = Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256)).ToLowerInvariant(),
            ["not_before"] = cert.NotBefore.ToUniversalTime().ToString("O"),
            ["not_after"] = cert.NotAfter.ToUniversalTime().ToString("O"),
            ["pem"] = pem,
        };
    }

    // ── 3. list_provisioners ────────────────────────────────────────────────
    [McpServerTool(Name = "list_provisioners")]
    [Description("List all configured provisioners on the step-ca.")]
    public static async Task<JsonObject> ListProvisioners(StepCli step, StepCaOptions opts, CancellationToken ct)
    {
        var r = await step.RunAsync(["ca", "provisioner", "list", "--ca-url", opts.CaUrl, "--root", opts.CaRootCertPath], ct);
        if (r.ExitCode != 0)
            return new JsonObject { ["ok"] = false, ["error"] = StepCaErrors.FromStepResult(r) };

        JsonNode? parsed;
        try { parsed = JsonNode.Parse(r.Stdout); }
        catch { parsed = null; }
        if (parsed is not JsonArray arr)
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = "non-JSON response",
                ["raw"] = r.Stdout.Length <= 400 ? r.Stdout : r.Stdout[..400],
            };
        }

        var summary = new JsonArray();
        foreach (var p in arr)
        {
            summary.Add(new JsonObject
            {
                ["name"] = p?["name"]?.GetValue<string>(),
                ["type"] = p?["type"]?.GetValue<string>(),
            });
        }
        return new JsonObject { ["ok"] = true, ["provisioners"] = summary, ["count"] = summary.Count };
    }

    // ── 4. issue_certificate (MUTATES) ─────────────────────────────────────
    [McpServerTool(Name = "issue_certificate")]
    [Description("Mint a new TLS server certificate from the step-ca via the issuer JWK provisioner.")]
    public static async Task<JsonObject> IssueCertificate(
        StepCli step, StepCaOptions opts,
        [Description("Common name (CN) for the cert")] string common_name,
        [Description("Optional Subject Alternative Names")] string[]? sans = null,
        CancellationToken ct = default)
    {
        // Fail-fast input validation BEFORE any subprocess is spawned. Returns a
        // structured error; never throws, never leaks.
        if (StepCaValidation.ValidateCommonName(common_name) is { } cnError)
            return new JsonObject { ["ok"] = false, ["error"] = cnError };
        if (StepCaValidation.ValidateSans(sans) is { } sanError)
            return new JsonObject { ["ok"] = false, ["error"] = sanError };

        var sansList = sans ?? Array.Empty<string>();
        var td = Directory.CreateTempSubdirectory("stepca-mcp-").FullName;
        try
        {
            var certPath = Path.Combine(td, "cert.pem");
            var keyPath = Path.Combine(td, "key.pem");
            var args = new List<string>
            {
                "ca", "certificate", common_name, certPath, keyPath,
                "--provisioner", opts.IssuerProvisioner,
                "--provisioner-password-file", opts.IssuerPasswordFile,
                "--ca-url", opts.CaUrl,
                "--root", opts.CaRootCertPath,
            };
            foreach (var s in sansList) { args.Add("--san"); args.Add(s); }

            var r = await step.RunAsync(args.ToArray(), ct);
            if (r.ExitCode != 0)
                return new JsonObject { ["ok"] = false, ["error"] = StepCaErrors.FromStepResult(r) };

            var certPem = await File.ReadAllTextAsync(certPath, ct);
            var keyPem = await File.ReadAllTextAsync(keyPath, ct);
            // Dispose the X509Certificate2 (unmanaged crypto handle).
            using var cert = X509Certificate2.CreateFromPem(certPem);

            var sansArr = new JsonArray();
            foreach (var s in sansList) sansArr.Add(s);

            return new JsonObject
            {
                ["ok"] = true,
                ["common_name"] = common_name,
                ["subject"] = cert.SubjectName.Name,
                ["sans"] = sansArr,
                ["serial_number"] = SerialNumberAsDecimal(cert),
                ["fingerprint_sha256"] = Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256)).ToLowerInvariant(),
                ["not_before"] = cert.NotBefore.ToUniversalTime().ToString("O"),
                ["not_after"] = cert.NotAfter.ToUniversalTime().ToString("O"),
                ["certificate_pem"] = certPem,
                ["private_key_pem"] = keyPem,
            };
        }
        finally
        {
            try { Directory.Delete(td, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── 5. revoke_certificate (MUTATES) ─────────────────────────────────────
    [McpServerTool(Name = "revoke_certificate")]
    [Description("Revoke a previously-issued certificate by serial number.")]
    public static async Task<JsonObject> RevokeCertificate(
        StepCli step, StepCaOptions opts,
        [Description("Serial number of the certificate to revoke")] string serial_number,
        [Description("Reason text")] string reason = "unspecified",
        [Description("CRL reason code (RFC 5280)")] int reason_code = 0,
        CancellationToken ct = default)
    {
        // Fail-fast input validation BEFORE any subprocess is spawned. Empty /
        // whitespace still yields the exact "serial_number is required" message
        // (behaviour parity); malformed serials and out-of-range reason codes are
        // now rejected too instead of being handed to `step`.
        if (StepCaValidation.ValidateSerialNumber(serial_number) is { } serialError)
            return new JsonObject { ["ok"] = false, ["error"] = serialError };
        if (StepCaValidation.ValidateReasonCode(reason_code) is { } reasonError)
            return new JsonObject { ["ok"] = false, ["error"] = reasonError };

        var args = new[]
        {
            "ca", "revoke", serial_number,
            "--provisioner", opts.IssuerProvisioner,
            "--provisioner-password-file", opts.IssuerPasswordFile,
            "--reason", reason,
            "--reasonCode", reason_code.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--ca-url", opts.CaUrl,
            "--root", opts.CaRootCertPath,
        };
        var r = await step.RunAsync(args, ct);
        if (r.ExitCode != 0)
            return new JsonObject { ["ok"] = false, ["error"] = StepCaErrors.FromStepResult(r) };

        return new JsonObject
        {
            ["ok"] = true,
            ["serial_number"] = serial_number,
            ["reason"] = reason,
            ["reason_code"] = reason_code,
            ["ca_response"] = r.Stdout.Trim(),
        };
    }

    // ── 6. inspect_certificate ──────────────────────────────────────────────
    /// <summary>Maximum accepted PEM string length (64 KiB).</summary>
    /// <remarks>
    /// Caps input to prevent an LOH allocation on a large pasted blob. A standard
    /// cert is ~2 KiB; 64 KiB is generous for any real X.509 certificate (incl.
    /// ones with large SAN extensions).
    /// </remarks>
    private const int MaxInspectPemBytes = 65_536;

    [McpServerTool(Name = "inspect_certificate")]
    [Description("Parse a PEM certificate string and return its metadata.")]
    public static JsonObject InspectCertificate(
        [Description("PEM-encoded certificate")] string certificate_pem)
    {
        // Refuse oversize input before CreateFromPem does a full-buffer
        // BEGIN/END scan.
        if (certificate_pem.Length > MaxInspectPemBytes)
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = $"certificate_pem too large ({certificate_pem.Length} bytes; max {MaxInspectPemBytes})",
            };
        }

        // Wrap in `using` so the unmanaged crypto handle is released
        // deterministically. CreateFromPem returns the FIRST certificate in a chain.
        X509Certificate2 certHandle;
        try { certHandle = X509Certificate2.CreateFromPem(certificate_pem); }
        catch (Exception e) { return new JsonObject { ["ok"] = false, ["error"] = $"could not parse PEM: {e.Message}" }; }

        using var cert = certHandle;

        var sans = new JsonArray();
        foreach (var ext in cert.Extensions)
        {
            if (ext is X509SubjectAlternativeNameExtension san)
            {
                foreach (var dns in san.EnumerateDnsNames()) sans.Add(dns);
                foreach (var ip in san.EnumerateIPAddresses()) sans.Add(ip.ToString());
            }
        }

        var now = DateTime.UtcNow;
        var remaining = (long)(cert.NotAfter.ToUniversalTime() - now).TotalSeconds;
        var pkType = cert.PublicKey.Oid?.FriendlyName ?? cert.PublicKey.GetType().Name;

        return new JsonObject
        {
            ["ok"] = true,
            ["subject"] = cert.SubjectName.Name,
            ["issuer"] = cert.IssuerName.Name,
            ["serial_number"] = SerialNumberAsDecimal(cert),
            ["subject_alt_names"] = sans,
            ["not_before"] = cert.NotBefore.ToUniversalTime().ToString("O"),
            ["not_after"] = cert.NotAfter.ToUniversalTime().ToString("O"),
            ["seconds_until_expiry"] = remaining,
            ["expired"] = remaining < 0,
            ["fingerprint_sha256"] = Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256)).ToLowerInvariant(),
            ["public_key_algorithm"] = pkType,
        };
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>Return the serial number as a decimal string.</summary>
    internal static string SerialNumberAsDecimal(X509Certificate2 cert)
    {
        // X509Certificate2.SerialNumber returns hex (no 0x prefix). Convert to BigInteger decimal.
        var hex = cert.SerialNumber;
        if (hex.Length % 2 != 0) hex = "0" + hex;
        var bytes = Convert.FromHexString(hex);
        // Reverse for little-endian; mark as unsigned by appending 0x00.
        Array.Reverse(bytes);
        var unsigned = new byte[bytes.Length + 1];
        Buffer.BlockCopy(bytes, 0, unsigned, 0, bytes.Length);
        return new System.Numerics.BigInteger(unsigned).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
