// ---------------------------------------------------------------------------
// GeminiValidationTests — the invalid-input -> structured-reason matrix for
// GeminiValidation. Mirrors the StepCa.Mcp exemplar (StepCaValidationTests):
// each Validate* returns null for a good value and a human-readable reason for a
// bad one, never throwing. NUL test inputs use the C# escape \0, never a literal
// NUL byte, so the source file diffs as text.
// ---------------------------------------------------------------------------
using Gemini.Mcp;
using Xunit;

namespace Gemini.Mcp.Tests;

public sealed class GeminiValidationTests
{
    // ── prompt (free-text content) ───────────────────────────────────────────

    [Fact]
    public void ValidatePrompt_accepts_a_normal_multiline_prompt()
    {
        // Newlines are legitimate in a prompt — they must NOT be rejected.
        Assert.Null(GeminiValidation.ValidatePrompt("line one\nline two\nline three"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ValidatePrompt_rejects_empty(string? prompt)
    {
        var reason = GeminiValidation.ValidatePrompt(prompt);
        Assert.NotNull(reason);
        Assert.Contains("required", reason);
    }

    [Fact]
    public void ValidatePrompt_rejects_a_nul_character()
    {
        var reason = GeminiValidation.ValidatePrompt("hello\0world");
        Assert.NotNull(reason);
        Assert.Contains("NUL", reason);
    }

    [Fact]
    public void ValidatePrompt_rejects_an_over_long_prompt()
    {
        var huge = new string('x', GeminiValidation.MaxPromptLength + 1);
        var reason = GeminiValidation.ValidatePrompt(huge);
        Assert.NotNull(reason);
        Assert.Contains("too long", reason);
    }

    [Fact]
    public void ValidatePrompt_uses_the_supplied_field_name()
    {
        var reason = GeminiValidation.ValidatePrompt(null, "query");
        Assert.NotNull(reason);
        Assert.StartsWith("query", reason);
    }

    // ── model / depth / aspect_ratio enums ───────────────────────────────────

    [Theory]
    [InlineData("auto")]
    [InlineData("pro")]
    [InlineData("flash")]
    [InlineData("flash-lite")]
    [InlineData("")]   // empty -> defaulted by the tool, treated as ok
    [InlineData(null)]
    public void ValidateModel_accepts_allowed_values(string? model) =>
        Assert.Null(GeminiValidation.ValidateModel(model));

    [Theory]
    [InlineData("gpt-4")]
    [InlineData("--yolo")]
    [InlineData("PRO")]
    public void ValidateModel_rejects_unknown_values(string model)
    {
        var reason = GeminiValidation.ValidateModel(model);
        Assert.NotNull(reason);
        Assert.Contains("invalid", reason);
    }

    [Theory]
    [InlineData("quick")]
    [InlineData("standard")]
    [InlineData("deep")]
    [InlineData(null)]
    public void ValidateDepth_accepts_allowed_values(string? depth) =>
        Assert.Null(GeminiValidation.ValidateDepth(depth));

    [Theory]
    [InlineData("shallow")]
    [InlineData("--evil")]
    public void ValidateDepth_rejects_unknown_values(string depth) =>
        Assert.NotNull(GeminiValidation.ValidateDepth(depth));

    [Theory]
    [InlineData("1:1")]
    [InlineData("16:9")]
    [InlineData(null)]  // optional
    [InlineData("")]
    public void ValidateAspectRatio_accepts_allowed_or_absent(string? ar) =>
        Assert.Null(GeminiValidation.ValidateAspectRatio(ar));

    [Theory]
    [InlineData("2:1")]
    [InlineData("square")]
    public void ValidateAspectRatio_rejects_unknown_values(string ar) =>
        Assert.NotNull(GeminiValidation.ValidateAspectRatio(ar));

    // ── path (image_path / file_paths) ───────────────────────────────────────

    [Fact]
    public void ValidatePath_accepts_a_normal_absolute_path() =>
        Assert.Null(GeminiValidation.ValidatePath("/var/data/pic.png", "image_path"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidatePath_rejects_empty(string? path) =>
        Assert.NotNull(GeminiValidation.ValidatePath(path, "image_path"));

    [Fact]
    public void ValidatePath_rejects_a_newline()
    {
        var reason = GeminiValidation.ValidatePath("/var/data/a\nb.png", "image_path");
        Assert.NotNull(reason);
        Assert.Contains("control characters", reason);
    }

    [Fact]
    public void ValidatePath_rejects_a_leading_dash_so_it_cannot_be_a_cli_flag()
    {
        var reason = GeminiValidation.ValidatePath("-rf", "image_path");
        Assert.NotNull(reason);
        Assert.Contains("'-'", reason);
    }

    [Fact]
    public void ValidateFilePaths_rejects_null_or_empty_list()
    {
        Assert.NotNull(GeminiValidation.ValidateFilePaths(null));
        Assert.NotNull(GeminiValidation.ValidateFilePaths(Array.Empty<string>()));
    }

    [Fact]
    public void ValidateFilePaths_reports_the_offending_index()
    {
        var reason = GeminiValidation.ValidateFilePaths(new[] { "/ok/a.txt", "-evil" });
        Assert.NotNull(reason);
        Assert.Contains("file_paths[1]", reason);
    }

    [Fact]
    public void ValidateFilePaths_rejects_too_many()
    {
        var many = Enumerable.Range(0, GeminiValidation.MaxFilePaths + 1).Select(i => $"/f/{i}").ToArray();
        var reason = GeminiValidation.ValidateFilePaths(many);
        Assert.NotNull(reason);
        Assert.Contains("too many", reason);
    }

    // ── id (session_id / task_id) — path-traversal guard ─────────────────────

    [Fact]
    public void ValidateId_accepts_a_uuid() =>
        Assert.Null(GeminiValidation.ValidateId(Guid.NewGuid().ToString(), "session_id"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ValidateId_rejects_empty(string? id) =>
        Assert.NotNull(GeminiValidation.ValidateId(id, "task_id"));

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData(".")]
    public void ValidateId_rejects_path_traversal(string id)
    {
        var reason = GeminiValidation.ValidateId(id, "task_id");
        Assert.NotNull(reason);
    }

    [Fact]
    public void ValidateId_rejects_a_control_character()
    {
        var reason = GeminiValidation.ValidateId("abc\0def", "task_id");
        Assert.NotNull(reason);
        Assert.Contains("control characters", reason);
    }

    // ── focus (optional) ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("investigate a flaky test")]
    public void ValidateFocus_accepts_absent_or_normal(string? focus) =>
        Assert.Null(GeminiValidation.ValidateFocus(focus));

    [Fact]
    public void ValidateFocus_rejects_over_long()
    {
        var huge = new string('x', GeminiValidation.MaxFocusLength + 1);
        Assert.NotNull(GeminiValidation.ValidateFocus(huge));
    }
}
