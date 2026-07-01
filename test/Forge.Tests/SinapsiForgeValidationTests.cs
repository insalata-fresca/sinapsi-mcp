using Sinapsi.Forge.Tools;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// Pins the input-validation guard shared by the whole git-forge tool surface. Every
/// parameter that reaches a forge URL segment is validated BEFORE any HTTP call; these
/// cases prove the rejection rules fire and that a clean value passes. ASCII banners and
/// a real C# NUL (<c>\0</c>) are used so the file diffs as TEXT, not binary.
/// </summary>
public sealed class SinapsiForgeValidationTests
{
    // ── path-segment identifiers (owner / repo / username / org) ────────────────

    [Theory]
    [InlineData(null, "owner is required")]
    [InlineData("", "owner is required")]
    [InlineData("   ", "owner is required")]
    [InlineData("-danger", "owner must not start with '-'")]
    [InlineData("a/b", "owner must not contain a path separator")]
    [InlineData("a\\b", "owner must not contain a path separator")]
    [InlineData("bad\tname", "owner contains control characters")]
    [InlineData("bad\nname", "owner contains control characters")]
    [InlineData("nul\0here", "owner contains control characters")]   // C# NUL, not a text \0
    public void ValidateSegment_rejects_bad_values(string? value, string expectedFragment)
    {
        var reason = SinapsiForgeValidation.ValidateSegment(value, "owner");
        Assert.NotNull(reason);
        Assert.Contains(expectedFragment, reason);
    }

    [Fact]
    public void ValidateSegment_rejects_over_length()
    {
        var reason = SinapsiForgeValidation.ValidateSegment(new string('a', SinapsiForgeValidation.MaxSegmentLength + 1), "repo");
        Assert.NotNull(reason);
        Assert.Contains("too long", reason);
    }

    [Theory]
    [InlineData("ste")]
    [InlineData("sinapsi-mcp")]
    [InlineData("Some_Org.42")]
    public void ValidateSegment_accepts_clean_values(string value)
        => Assert.Null(SinapsiForgeValidation.ValidateSegment(value, "owner"));

    [Fact]
    public void ValidateOwnerRepo_returns_first_failure_then_null_when_both_ok()
    {
        Assert.Equal("owner is required", SinapsiForgeValidation.ValidateOwnerRepo(null, "r"));
        Assert.Contains("repo", SinapsiForgeValidation.ValidateOwnerRepo("o", "-bad"));
        Assert.Null(SinapsiForgeValidation.ValidateOwnerRepo("o", "r"));
    }

    // ── refs / branches / tags (path separators ALLOWED, leading-dash NOT) ──────

    [Theory]
    [InlineData("refs/heads/main")]   // hierarchical ref is fine
    [InlineData("feature/x")]
    [InlineData("v1.2.3")]
    public void ValidateRef_accepts_hierarchical_refs(string value)
        => Assert.Null(SinapsiForgeValidation.ValidateRef(value, "branch"));

    [Theory]
    [InlineData("-force", "branch must not start with '-'")]
    [InlineData("bad\nref", "branch contains control characters")]
    public void ValidateRef_rejects_bad_values(string value, string expectedFragment)
        => Assert.Contains(expectedFragment, SinapsiForgeValidation.ValidateRef(value, "branch"));

    [Fact]
    public void ValidateRef_optional_allows_null_but_required_does_not()
    {
        Assert.Null(SinapsiForgeValidation.ValidateRef(null, "from_branch", required: false));
        Assert.Equal("branch is required", SinapsiForgeValidation.ValidateRef(null, "branch"));
    }

    // ── paths (separators ALLOWED, .. traversal + leading-dash NOT) ─────────────

    [Theory]
    [InlineData("docs/a.md")]
    [InlineData("src/servers/Forge.Mcp/Program.cs")]
    public void ValidatePath_accepts_normal_paths(string value)
        => Assert.Null(SinapsiForgeValidation.ValidatePath(value));

    [Theory]
    [InlineData("../etc/passwd", "must not contain a '..' traversal")]
    [InlineData("a/../../b", "must not contain a '..' traversal")]
    [InlineData("-x", "must not start with '-'")]
    [InlineData("bad\0path", "contains control characters")]
    public void ValidatePath_rejects_bad_paths(string value, string expectedFragment)
        => Assert.Contains(expectedFragment, SinapsiForgeValidation.ValidatePath(value));

    // ── limits + positive ids ───────────────────────────────────────────────────

    [Theory]
    [InlineData(0, "must be positive")]
    [InlineData(-5, "must be positive")]
    [InlineData(1001, "too large")]
    public void ValidateLimit_rejects_out_of_range(int limit, string expectedFragment)
        => Assert.Contains(expectedFragment, SinapsiForgeValidation.ValidateLimit(limit));

    [Fact]
    public void ValidateLimit_accepts_in_range() => Assert.Null(SinapsiForgeValidation.ValidateLimit(30));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ValidatePositiveId_rejects_non_positive(long id)
        => Assert.Contains("must be a positive id", SinapsiForgeValidation.ValidatePositiveId(id, "number"));

    [Fact]
    public void ValidatePositiveId_accepts_positive() => Assert.Null(SinapsiForgeValidation.ValidatePositiveId(7, "number"));

    // ── text fields (single-line reject newlines, multiline allow) ──────────────

    [Fact]
    public void ValidateText_required_single_line_rejects_empty_and_control()
    {
        Assert.Equal("title is required", SinapsiForgeValidation.ValidateText(null, "title", 100));
        Assert.Contains("contains control characters", SinapsiForgeValidation.ValidateText("a\nb", "title", 100));
    }

    [Fact]
    public void ValidateText_multiline_allows_newlines()
        => Assert.Null(SinapsiForgeValidation.ValidateText("line1\nline2", "body", 100, allowNewlines: true));

    [Fact]
    public void ValidateQuery_requires_non_empty_and_caps_length()
    {
        Assert.Equal("query is required", SinapsiForgeValidation.ValidateQuery(""));
        Assert.Contains("too long", SinapsiForgeValidation.ValidateQuery(new string('x', SinapsiForgeValidation.MaxQueryLength + 1)));
        Assert.Null(SinapsiForgeValidation.ValidateQuery("hello"));
    }
}
