using Xunit;

namespace StepCa.Mcp.Tests;

/// <summary>
/// <see cref="StepCaErrors"/> guarantees a caller-facing error message never
/// leaks key material or credentials and is length-capped. These are the
/// fail-safe tests for the "no secret in an error" contract.
/// </summary>
public sealed class StepCaErrorsTests
{
    [Fact]
    public void Sanitize_RedactsPrivateKeyBlock()
    {
        const string leak =
            "error signing:\n-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA\n-----END RSA PRIVATE KEY-----\n";

        var clean = StepCaErrors.Sanitize(leak);

        Assert.DoesNotContain("MIIEowIBAAKCAQEA", clean);
        Assert.DoesNotContain("BEGIN RSA PRIVATE KEY", clean);
        Assert.Contains("[redacted]", clean);
        Assert.Contains("error signing", clean); // surrounding diagnostic preserved
    }

    [Fact]
    public void Sanitize_RedactsEcAndPkcs8KeyBlocks()
    {
        const string ec = "-----BEGIN EC PRIVATE KEY-----\nAAAA\n-----END EC PRIVATE KEY-----";
        const string pkcs8 = "-----BEGIN PRIVATE KEY-----\nBBBB\n-----END PRIVATE KEY-----";

        Assert.DoesNotContain("AAAA", StepCaErrors.Sanitize(ec));
        Assert.DoesNotContain("BBBB", StepCaErrors.Sanitize(pkcs8));
    }

    [Theory]
    [InlineData("password=hunter2", "hunter2")]
    [InlineData("provisioner secret: s3cr3t-value", "s3cr3t-value")]
    [InlineData("Authorization: Bearer abc.def.ghi", "abc.def.ghi")]
    [InlineData("api_key=AKIAEXAMPLE", "AKIAEXAMPLE")]
    public void Sanitize_RedactsCredentialAssignments(string input, string secret)
    {
        var clean = StepCaErrors.Sanitize(input);

        Assert.DoesNotContain(secret, clean);
        Assert.Contains("[redacted]", clean);
    }

    [Fact]
    public void Sanitize_CapsLength()
    {
        var huge = new string('x', StepCaErrors.MaxErrorLength + 5_000);

        var clean = StepCaErrors.Sanitize(huge);

        Assert.True(clean.Length <= StepCaErrors.MaxErrorLength + 32);
        Assert.EndsWith("[truncated]", clean);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sanitize_EmptyInput_GivesGenericMessage(string? input) =>
        Assert.Equal("step CLI failed with no diagnostic output", StepCaErrors.Sanitize(input));

    [Fact]
    public void FromStepResult_PrefersStderr_ThenScrubs()
    {
        var r = new StepResult(
            ExitCode: 1,
            Stdout: "",
            Stderr: "fatal: token=SECRETTOKEN expired",
            TimedOut: false);

        var msg = StepCaErrors.FromStepResult(r);

        Assert.DoesNotContain("SECRETTOKEN", msg);
        Assert.Contains("fatal", msg);
    }

    [Fact]
    public void FromStepResult_FallsBackToStdout_WhenStderrEmpty()
    {
        var r = new StepResult(ExitCode: 1, Stdout: "provisioner not found", Stderr: "", TimedOut: false);

        Assert.Equal("provisioner not found", StepCaErrors.FromStepResult(r));
    }
}
