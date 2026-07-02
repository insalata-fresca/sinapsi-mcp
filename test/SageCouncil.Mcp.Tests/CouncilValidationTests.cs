using Xunit;

namespace SageCouncil.Mcp.Tests;

// -----------------------------------------------------------------------------
// Unit tests for CouncilValidation — the fail-fast input guard. Each method
// returns null on valid input, else a human-readable reason (never throws).
// NUL is expressed with the C# escape \0, never a literal NUL byte, so every
// source file diffs as TEXT.
// -----------------------------------------------------------------------------

public sealed class CouncilValidationTests
{
    // ── prompt ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidatePrompt_rejects_missing(string? prompt)
    {
        var r = CouncilValidation.ValidatePrompt(prompt);
        Assert.NotNull(r);
        Assert.Contains("required", r);
    }

    [Fact]
    public void ValidatePrompt_rejects_a_nul_control_char()
    {
        var r = CouncilValidation.ValidatePrompt("hard question\0with nul");
        Assert.Equal("prompt contains control characters", r);
    }

    [Fact]
    public void ValidatePrompt_allows_multiline_free_text()
    {
        // Newlines + tabs are legitimate in a free-text prompt.
        Assert.Null(CouncilValidation.ValidatePrompt("line one\n\tindented line two\r\nline three"));
    }

    [Fact]
    public void ValidatePrompt_rejects_an_oversize_blob()
    {
        var big = new string('x', CouncilValidation.MaxPromptLength + 1);
        var r = CouncilValidation.ValidatePrompt(big);
        Assert.NotNull(r);
        Assert.Contains("too long", r);
    }

    // ── focus ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ValidateFocus_rejects_missing(string? focus)
    {
        Assert.Contains("required", CouncilValidation.ValidateFocus(focus)!);
    }

    [Theory]
    [InlineData("bad\nfocus")]
    [InlineData("bad\0focus")]
    public void ValidateFocus_rejects_control_or_newline(string focus)
    {
        Assert.Equal("focus contains control characters", CouncilValidation.ValidateFocus(focus));
    }

    [Fact]
    public void ValidateFocus_rejects_a_leading_dash()
    {
        Assert.Equal("focus must not start with '-'", CouncilValidation.ValidateFocus("-rf"));
    }

    [Theory]
    [InlineData("general")]
    [InlineData("code-review")]
    [InlineData("architecture")]
    public void ValidateFocus_accepts_a_clean_identifier(string focus)
    {
        Assert.Null(CouncilValidation.ValidateFocus(focus));
    }

    // ── members ──────────────────────────────────────────────────────────────

    [Fact]
    public void ValidateMembers_accepts_null_and_empty_as_the_default_roster()
    {
        Assert.Null(CouncilValidation.ValidateMembers(null));
        Assert.Null(CouncilValidation.ValidateMembers(Array.Empty<string>()));
    }

    [Fact]
    public void ValidateMembers_rejects_an_empty_entry()
    {
        var r = CouncilValidation.ValidateMembers(new[] { "claude-research", "  " });
        Assert.NotNull(r);
        Assert.Contains("#2 is empty", r);
    }

    [Fact]
    public void ValidateMembers_rejects_a_control_char_entry()
    {
        var r = CouncilValidation.ValidateMembers(new[] { "gemini\0research" });
        Assert.Contains("contains control characters", r!);
    }

    [Fact]
    public void ValidateMembers_rejects_a_leading_dash_entry()
    {
        var r = CouncilValidation.ValidateMembers(new[] { "-rf" });
        Assert.Contains("must not start with '-'", r!);
    }

    [Fact]
    public void ValidateMembers_rejects_too_many()
    {
        var many = Enumerable.Range(0, CouncilValidation.MaxMembers + 1).Select(i => "m" + i).ToArray();
        var r = CouncilValidation.ValidateMembers(many);
        Assert.Contains("too many members", r!);
    }

    // ── job_id ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ValidateJobId_rejects_missing(string? jobId)
    {
        Assert.Contains("required", CouncilValidation.ValidateJobId(jobId)!);
    }

    [Fact]
    public void ValidateJobId_rejects_a_control_char()
    {
        Assert.Equal("job_id contains control characters", CouncilValidation.ValidateJobId("council-\0abc"));
    }

    [Fact]
    public void ValidateJobId_accepts_a_well_formed_id()
    {
        Assert.Null(CouncilValidation.ValidateJobId("council-0123456789ab"));
    }
}
