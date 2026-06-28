using Xunit;

namespace StepCa.Mcp.Tests;

/// <summary>
/// End-to-end guard tests for the two MUTATING tools. The CLI points at a
/// non-existent binary, so if validation failed to short-circuit, the call would
/// surface a process-start error instead of the precise validation message. Each
/// assertion therefore proves both the rejection reason AND that no subprocess
/// was spawned.
/// </summary>
public sealed class MutatingToolGuardTests
{
    private static (StepCli cli, StepCaOptions opts) Harness() =>
        (new StepCli(Opts), Opts);

    private static readonly StepCaOptions Opts = new(
        CaUrl: "https://ca.example.com:9000",
        CaRootCertPath: "/etc/step-ca-mcp/root_ca.crt",
        CaFingerprint: "",
        StepBin: "/nonexistent/step", // never invoked on a guard path
        IssuerProvisioner: "mcp-issuer",
        IssuerPasswordFile: "/etc/step-ca-mcp/mcp-issuer-password.txt",
        SubprocessTimeoutMs: 30_000);

    // ── issue_certificate ───────────────────────────────────────────────────
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Issue_EmptyCommonName_RejectedBeforeSubprocess(string cn)
    {
        var (cli, opts) = Harness();
        var r = await StepCaTools.IssueCertificate(cli, opts, cn);

        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.Equal("common_name is required", r["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Issue_LeadingDashCommonName_Rejected()
    {
        var (cli, opts) = Harness();
        var r = await StepCaTools.IssueCertificate(cli, opts, "--ca-url");

        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.Contains("must not start with '-'", r["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Issue_EmptySanEntry_Rejected()
    {
        var (cli, opts) = Harness();
        var r = await StepCaTools.IssueCertificate(cli, opts, "node.example.com", new[] { "" });

        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.Contains("is empty", r["error"]!.GetValue<string>());
    }

    // ── revoke_certificate ──────────────────────────────────────────────────
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Revoke_EmptySerial_KeepsExactLegacyMessage(string serial)
    {
        var (cli, opts) = Harness();
        var r = await StepCaTools.RevokeCertificate(cli, opts, serial);

        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.Equal("serial_number is required", r["error"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("12xy")]
    [InlineData("-1")]
    [InlineData("0xZZ")]
    public async Task Revoke_MalformedSerial_RejectedBeforeSubprocess(string serial)
    {
        var (cli, opts) = Harness();
        var r = await StepCaTools.RevokeCertificate(cli, opts, serial);

        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.Contains("serial_number", r["error"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    public async Task Revoke_OutOfRangeReasonCode_Rejected(int code)
    {
        var (cli, opts) = Harness();
        var r = await StepCaTools.RevokeCertificate(cli, opts, "12345", "keyCompromise", code);

        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.Contains("out of range", r["error"]!.GetValue<string>());
    }
}
