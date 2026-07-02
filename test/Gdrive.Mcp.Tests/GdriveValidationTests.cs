using Xunit;

namespace Gdrive.Mcp.Tests;

/// <summary>
/// Unit tests for <see cref="GdriveValidation"/>: every tool parameter that
/// reaches the Drive API is checked here BEFORE any HTTP call. These assert that
/// valid input passes (returns <c>null</c>) and that each rejection reason is
/// produced for the matching malformed input.
/// </summary>
public sealed class GdriveValidationTests
{
    // ── file id ─────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("1a2B3c4D5e6F")]
    [InlineData("0AElfWBcd_shared_drive_id")]
    public void ValidateFileId_AcceptsValid(string id) =>
        Assert.Null(GdriveValidation.ValidateFileId(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateFileId_RejectsEmpty(string? id) =>
        Assert.Equal("fileId is required", GdriveValidation.ValidateFileId(id));

    [Fact]
    public void ValidateFileId_RejectsTooLong()
    {
        var id = new string('a', GdriveValidation.MaxIdLength + 1);
        Assert.Contains("too long", GdriveValidation.ValidateFileId(id)!);
    }

    [Theory]
    [InlineData("bad\nid")]
    [InlineData("bad\tid")]
    [InlineData("nul\0id")] // C# escape for NUL — never a literal NUL byte in source
    public void ValidateFileId_RejectsControlChars(string id) =>
        Assert.Contains("control characters", GdriveValidation.ValidateFileId(id)!);

    [Fact]
    public void ValidateFileId_RejectsLeadingDash() =>
        Assert.Contains("must not start with '-'", GdriveValidation.ValidateFileId("-flaglike")!);

    // ── folder id (optional) ────────────────────────────────────────────────
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ValidateFolderId_AllowsOmitted(string? id) =>
        Assert.Null(GdriveValidation.ValidateFolderId(id));

    [Fact]
    public void ValidateFolderId_AcceptsValid() =>
        Assert.Null(GdriveValidation.ValidateFolderId("0AElfWBcd"));

    [Fact]
    public void ValidateFolderId_RejectsWhitespaceOnly() =>
        Assert.Contains("whitespace", GdriveValidation.ValidateFolderId("   ")!);

    [Fact]
    public void ValidateFolderId_RejectsControlChars() =>
        Assert.Contains("control characters", GdriveValidation.ValidateFolderId("a\nb")!);

    [Fact]
    public void ValidateFolderId_RejectsLeadingDash() =>
        Assert.Contains("must not start with '-'", GdriveValidation.ValidateFolderId("-x")!);

    // ── query ───────────────────────────────────────────────────────────────
    [Fact]
    public void ValidateQuery_AcceptsValid() =>
        Assert.Null(GdriveValidation.ValidateQuery("name contains 'budget' and trashed = false"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateQuery_RejectsEmpty(string? q) =>
        Assert.Equal("query is required", GdriveValidation.ValidateQuery(q));

    [Fact]
    public void ValidateQuery_RejectsTooLong()
    {
        var q = new string('a', GdriveValidation.MaxQueryLength + 1);
        Assert.Contains("too long", GdriveValidation.ValidateQuery(q)!);
    }

    [Fact]
    public void ValidateQuery_RejectsControlChars() =>
        Assert.Contains("control characters", GdriveValidation.ValidateQuery("name = 'x'\r\ninjected")!);

    // ── name ────────────────────────────────────────────────────────────────
    [Fact]
    public void ValidateName_AcceptsValid() =>
        Assert.Null(GdriveValidation.ValidateName("report 2026.txt"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateName_RejectsEmpty(string? n) =>
        Assert.Equal("name is required", GdriveValidation.ValidateName(n));

    [Fact]
    public void ValidateName_RejectsTooLong()
    {
        var n = new string('a', GdriveValidation.MaxNameLength + 1);
        Assert.Contains("too long", GdriveValidation.ValidateName(n)!);
    }

    [Fact]
    public void ValidateName_RejectsControlChars() =>
        Assert.Contains("control characters", GdriveValidation.ValidateName("a\nb")!);

    // ── optional new name (update_file) ──────────────────────────────────────
    [Fact]
    public void ValidateOptionalNewName_AllowsNull() =>
        Assert.Null(GdriveValidation.ValidateOptionalNewName(null));

    [Fact]
    public void ValidateOptionalNewName_RejectsEmptyString() =>
        Assert.Equal("name is required", GdriveValidation.ValidateOptionalNewName("   "));

    // ── mime type ────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/pdf")]
    public void ValidateMimeType_AcceptsValid(string m) =>
        Assert.Null(GdriveValidation.ValidateMimeType(m));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ValidateMimeType_RejectsEmpty(string? m) =>
        Assert.Equal("mimeType is required", GdriveValidation.ValidateMimeType(m));

    [Fact]
    public void ValidateMimeType_RejectsControlChars() =>
        Assert.Contains("control characters", GdriveValidation.ValidateMimeType("text/\nplain")!);

    [Fact]
    public void ValidateMimeType_RejectsTooLong()
    {
        var m = new string('a', GdriveValidation.MaxMimeTypeLength + 1);
        Assert.Contains("too long", GdriveValidation.ValidateMimeType(m)!);
    }

    // ── content ──────────────────────────────────────────────────────────────
    [Fact]
    public void ValidateContent_AcceptsEmptyString() =>
        Assert.Null(GdriveValidation.ValidateContent("")); // an empty file is legitimate

    [Fact]
    public void ValidateContent_RejectsNull() =>
        Assert.Equal("content is required", GdriveValidation.ValidateContent(null));

    [Fact]
    public void ValidateContent_RejectsOversize()
    {
        var big = new string('x', GdriveValidation.MaxContentBytes + 1);
        Assert.Contains("too large", GdriveValidation.ValidateContent(big)!);
    }

    // ── optional new content (update_file) ───────────────────────────────────
    [Fact]
    public void ValidateOptionalNewContent_AllowsNull() =>
        Assert.Null(GdriveValidation.ValidateOptionalNewContent(null));

    [Fact]
    public void ValidateOptionalNewContent_AllowsEmpty() =>
        Assert.Null(GdriveValidation.ValidateOptionalNewContent(""));

    [Fact]
    public void ValidateOptionalNewContent_RejectsOversize()
    {
        var big = new string('x', GdriveValidation.MaxContentBytes + 1);
        Assert.Contains("too large", GdriveValidation.ValidateOptionalNewContent(big)!);
    }
}
