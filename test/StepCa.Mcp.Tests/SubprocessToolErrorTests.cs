using Xunit;

namespace StepCa.Mcp.Tests;

/// <summary>
/// Tool-level coverage for the "upstream/CLI failure → structured error" hardening
/// path. The guard suites (<see cref="MutatingToolGuardTests"/>,
/// <see cref="RevokeCertificateGuardTests"/>) only ever exercise the validation
/// short-circuit — they point <c>StepBin</c> at <c>/nonexistent/step</c> precisely
/// so no subprocess runs. Those tests therefore never reach the
/// <c>if (r.ExitCode != 0) return … StepCaErrors.FromStepResult(r)</c> bodies that
/// every subprocess tool's docstring promises ("Returns a structured error").
///
/// <para>
/// These tests close that gap WITHOUT a live CA by pointing <c>StepBin</c> at a
/// stock binary whose exit code / stdout we control:
/// <list type="bullet">
///   <item><c>/bin/false</c> — exits non-zero with no stdout, forcing the
///   <c>ExitCode != 0</c> branch (the structured-error envelope).</item>
///   <item><c>/bin/echo</c> — exits 0 with controllable stdout, forcing
///   <c>list_provisioners</c>' non-array "non-JSON response" guard and covering
///   <c>get_ca_health</c>'s success body.</item>
/// </list>
/// </para>
/// </summary>
public sealed class SubprocessToolErrorTests
{
    private static string Stock(string preferred, string fallbackName)
    {
        if (File.Exists(preferred)) return preferred;
        var alt = "/usr/bin/" + fallbackName;
        if (File.Exists(alt)) return alt;
        throw new InvalidOperationException($"no {fallbackName} binary on this host");
    }

    // /bin/false — always exits 1, no stdout. Forces the ExitCode != 0 branch.
    private static string FalseBin => Stock("/bin/false", "false");

    // /bin/echo — exits 0, echoes its args to stdout. Lets us inject arbitrary
    // (non-JSON / "ok") stdout into a tool body.
    private static string EchoBin => Stock("/bin/echo", "echo");

    private static (StepCli cli, StepCaOptions opts) Harness(string stepBin)
    {
        var opts = new StepCaOptions(
            CaUrl: "https://ca.example.com:9000",
            CaRootCertPath: "/etc/step-ca-mcp/root_ca.crt",
            CaFingerprint: "",
            StepBin: stepBin,
            IssuerProvisioner: "mcp-issuer",
            IssuerPasswordFile: "/etc/step-ca-mcp/mcp-issuer-password.txt",
            SubprocessTimeoutMs: 30_000);
        return (new StepCli(opts), opts);
    }

    // ── list_provisioners ───────────────────────────────────────────────────

    /// <summary>
    /// CLI exits non-zero → the tool returns {ok:false, error:&lt;sanitized&gt;}.
    /// Covers StepCaTools.cs lines 88-89 (the ExitCode != 0 branch).
    /// </summary>
    [Fact]
    public async Task ListProvisioners_CliFails_ReturnsStructuredError()
    {
        var (cli, opts) = Harness(FalseBin);

        var r = await StepCaTools.ListProvisioners(cli, opts, CancellationToken.None);

        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.NotNull(r["error"]);
        // /bin/false emits no diagnostics → FromStepResult falls back to the
        // no-output sentinel rather than throwing.
        Assert.Equal(
            "step CLI failed with no diagnostic output",
            r["error"]!.GetValue<string>());
    }

    /// <summary>
    /// CLI exits 0 but stdout is not a JSON array → the malformed-upstream guard
    /// fires. Covers StepCaTools.cs lines 92-102 (non-JSON / truncated raw).
    /// </summary>
    [Fact]
    public async Task ListProvisioners_NonJsonStdout_ReturnsNonJsonError()
    {
        var (cli, opts) = Harness(EchoBin);

        // echo writes the joined args + newline to stdout: a plain string, which
        // JsonNode.Parse rejects → `parsed is not JsonArray`.
        var r = await StepCaTools.ListProvisioners(cli, opts, CancellationToken.None);

        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.Equal("non-JSON response", r["error"]!.GetValue<string>());
        // The raw upstream stdout is surfaced (truncated to ≤400 bytes) for
        // diagnosability. echo emits the provisioner-list args; assert the field
        // is present and non-empty.
        Assert.NotNull(r["raw"]);
        var raw = r["raw"]!.GetValue<string>();
        Assert.NotEmpty(raw);
        Assert.True(raw.Length <= 400);
    }

    // ── issue_certificate (mutating) ─────────────────────────────────────────

    /// <summary>
    /// Valid CN clears validation, then the CLI exits non-zero → structured error.
    /// Covers StepCaTools.cs lines 149-150 (the ExitCode != 0 branch of the
    /// mutating issue path).
    /// </summary>
    [Fact]
    public async Task IssueCertificate_CliFails_ReturnsStructuredError()
    {
        var (cli, opts) = Harness(FalseBin);

        var r = await StepCaTools.IssueCertificate(
            cli, opts, "node.example.com", sans: null, ct: CancellationToken.None);

        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.NotNull(r["error"]);
        Assert.Equal(
            "step CLI failed with no diagnostic output",
            r["error"]!.GetValue<string>());
        // The error path must short-circuit BEFORE the cert.pem read; the success
        // fields must be absent.
        Assert.Null(r["certificate_pem"]);
        Assert.Null(r["private_key_pem"]);
    }

    // ── revoke_certificate (mutating) ────────────────────────────────────────

    /// <summary>
    /// Valid serial clears the guard, then the CLI exits non-zero → structured
    /// error. Covers StepCaTools.cs lines 210-211 (the ExitCode != 0 branch of
    /// the mutating revoke path).
    /// </summary>
    [Fact]
    public async Task RevokeCertificate_CliFails_ReturnsStructuredError()
    {
        var (cli, opts) = Harness(FalseBin);

        // Serial 12345 is a valid hex serial → clears ValidateSerialNumber.
        var r = await StepCaTools.RevokeCertificate(
            cli, opts, "12345", reason: "keyCompromise", reason_code: 1,
            ct: CancellationToken.None);

        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.NotNull(r["error"]);
        Assert.Equal(
            "step CLI failed with no diagnostic output",
            r["error"]!.GetValue<string>());
        // Success-only field must not appear on the error envelope.
        Assert.Null(r["ca_response"]);
    }

    /// <summary>
    /// The FromStepResult redaction the docstring promises is applied end-to-end at
    /// the tool level, not just in StepCaErrors' isolated unit tests. We drive a
    /// failing binary that emits a fake secret on stderr and assert it is scrubbed
    /// out of the tool's error envelope.
    /// </summary>
    [Fact]
    public async Task RevokeCertificate_CliFails_RedactsSecretFromStderr()
    {
        // RevokeCertificate controls the args, not the binary's behaviour, so we
        // point StepBin at a purpose-built script that emits a credential on stderr
        // and exits non-zero regardless of the args it is handed.
        var scriptDir = Directory.CreateTempSubdirectory("stepca-redact-").FullName;
        try
        {
            var script = Path.Combine(scriptDir, "fakestep.sh");
            await File.WriteAllTextAsync(
                script,
                "#!/bin/sh\n" +
                "echo 'error contacting CA' 1>&2\n" +
                "echo 'password: hunter2-supersecret' 1>&2\n" +
                "exit 1\n");
            // chmod +x
            var psi = new System.Diagnostics.ProcessStartInfo("/bin/chmod")
            {
                ArgumentList = { "+x", script },
                RedirectStandardError = true,
            };
            using (var chmod = System.Diagnostics.Process.Start(psi)!) await chmod.WaitForExitAsync();

            var scriptOpts = new StepCaOptions(
                CaUrl: "https://ca.example.com:9000",
                CaRootCertPath: "/etc/step-ca-mcp/root_ca.crt",
                CaFingerprint: "",
                StepBin: script,
                IssuerProvisioner: "mcp-issuer",
                IssuerPasswordFile: "/etc/step-ca-mcp/mcp-issuer-password.txt",
                SubprocessTimeoutMs: 30_000);
            var scriptCli = new StepCli(scriptOpts);

            var r = await StepCaTools.RevokeCertificate(
                scriptCli, scriptOpts, "12345", reason: "keyCompromise", reason_code: 1,
                ct: CancellationToken.None);

            Assert.False(r["ok"]!.GetValue<bool>());
            var err = r["error"]!.GetValue<string>();
            // The diagnostic text survives…
            Assert.Contains("error contacting CA", err);
            // …but the secret value is redacted by FromStepResult/Sanitize.
            Assert.DoesNotContain("hunter2-supersecret", err);
            Assert.Contains("[redacted]", err);
        }
        finally
        {
            try { Directory.Delete(scriptDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── get_ca_health ────────────────────────────────────────────────────────

    /// <summary>
    /// Drives the whole get_ca_health body (StepCaTools.cs 21-38), which was 0%
    /// covered. With StepBin=/bin/echo, `ca health …` echoes its args (not "ok"),
    /// so ok==false; `version` echoes its arg too. We assert the envelope shape and
    /// the exact-match-on-"ok" semantics the body documents.
    /// </summary>
    [Fact]
    public async Task GetCaHealth_NonOkStdout_ReturnsNotHealthyEnvelope()
    {
        var (cli, opts) = Harness(EchoBin);

        var r = await StepCaTools.GetCaHealth(cli, opts, CancellationToken.None);

        // echo exits 0 but stdout is the arg list, not the literal "ok" → the
        // exact-match guard keeps ok==false (the Contains() false-positive the
        // comment warns about is avoided).
        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.Equal(opts.CaUrl, r["ca_url"]!.GetValue<string>());
        Assert.Equal(opts.CaRootCertPath, r["root_certificate_path"]!.GetValue<string>());
        // The neutral test root cert path does not exist on the build host.
        Assert.False(r["root_certificate_present"]!.GetValue<bool>());
        // The body populates step_health_output from trimmed stdout.
        Assert.NotNull(r["step_health_output"]);
    }

    /// <summary>
    /// The success leg of get_ca_health: a binary that prints exactly "ok" on
    /// stdout (and exits 0) makes the exact-match guard report ok==true.
    /// </summary>
    [Fact]
    public async Task GetCaHealth_OkStdout_ReportsHealthy()
    {
        // A purpose-built script that prints "ok" regardless of args, so both the
        // `ca health` and `version` invocations exit 0 with "ok" stdout.
        var scriptDir = Directory.CreateTempSubdirectory("stepca-health-").FullName;
        try
        {
            var script = Path.Combine(scriptDir, "okstep.sh");
            await File.WriteAllTextAsync(script, "#!/bin/sh\necho ok\nexit 0\n");
            var psi = new System.Diagnostics.ProcessStartInfo("/bin/chmod")
            {
                ArgumentList = { "+x", script },
                RedirectStandardError = true,
            };
            using (var chmod = System.Diagnostics.Process.Start(psi)!) await chmod.WaitForExitAsync();

            var opts = new StepCaOptions(
                CaUrl: "https://ca.example.com:9000",
                CaRootCertPath: "/etc/step-ca-mcp/root_ca.crt",
                CaFingerprint: "",
                StepBin: script,
                IssuerProvisioner: "mcp-issuer",
                IssuerPasswordFile: "/etc/step-ca-mcp/mcp-issuer-password.txt",
                SubprocessTimeoutMs: 30_000);
            var cli = new StepCli(opts);

            var r = await StepCaTools.GetCaHealth(cli, opts, CancellationToken.None);

            Assert.True(r["ok"]!.GetValue<bool>());
            Assert.Equal("ok", r["step_health_output"]!.GetValue<string>());
            Assert.Equal(opts.CaUrl, r["ca_url"]!.GetValue<string>());
        }
        finally
        {
            try { Directory.Delete(scriptDir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
