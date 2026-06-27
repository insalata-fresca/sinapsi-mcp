using Sinapsi.Indexer;
using Xunit;

namespace Sinapsi.Indexer.Tests;

/// <summary>
/// The scanner turns a synced checkout into <see cref="Document"/>s: it classifies
/// each markdown file into a coarse kind, derives a scope bucket for the learnings
/// source, extracts a title, and content-hashes the body for idempotent upserts.
/// These pin that pure logic (no git / no DB needed).
/// </summary>
public sealed class SourceScannerTests
{
    private const string Learnings = "learnings";

    // ── ClassifyKind: the learnings source is always "learning" ─────────────────

    [Fact]
    public void Files_in_the_learnings_source_are_classified_learning()
    {
        Assert.Equal("learning", SourceScanner.ClassifyKind(Learnings, "global/foo.md", Learnings));
        Assert.Equal("learning", SourceScanner.ClassifyKind(Learnings, "projects/acme/bar.md", Learnings));
    }

    [Theory]
    [InlineData("patterns/create-thing.md", "pattern")]
    [InlineData("decisions.md", "decision")]
    [InlineData("decisions/0001-pick-x.md", "decision")]
    [InlineData("conventions.md", "convention")]
    [InlineData("caveats.md", "caveat")]
    [InlineData("backlog.md", "backlog")]
    [InlineData("scopes/01-thing.md", "scope")]
    [InlineData("state/host-1.md", "state")]
    [InlineData("docs/overview.md", "doc")]
    [InlineData("README.md", "doc")]
    public void NonLearning_files_classify_by_path_prefix(string rel, string expected)
    {
        Assert.Equal(expected, SourceScanner.ClassifyKind("docs", rel, Learnings));
    }

    [Fact]
    public void Learnings_source_name_is_configurable_not_hardcoded()
    {
        // A custom learnings source name still wins over the path heuristic.
        Assert.Equal("learning", SourceScanner.ClassifyKind("kb", "docs/x.md", learningsSource: "kb"));
        // And a repo NOT named the learnings source is classified by path.
        Assert.Equal("doc", SourceScanner.ClassifyKind("kb", "docs/x.md", learningsSource: "learnings"));
    }

    // ── ScopeOf: first path segment, or "global" at the root ────────────────────

    [Theory]
    [InlineData("global/x.md", "global")]
    [InlineData("projects/acme/y.md", "projects")]
    [InlineData("at-root.md", "global")]
    public void ScopeOf_takes_the_first_path_segment(string rel, string expected)
    {
        Assert.Equal(expected, SourceScanner.ScopeOf(rel));
    }

    // ── ExtractTitle: first markdown H1, else the filename ──────────────────────

    [Fact]
    public void ExtractTitle_uses_the_first_h1()
    {
        Assert.Equal("Hello World", SourceScanner.ExtractTitle("intro\n# Hello World\nbody", "a/b.md"));
    }

    [Fact]
    public void ExtractTitle_falls_back_to_the_filename_without_extension()
    {
        Assert.Equal("notes", SourceScanner.ExtractTitle("no heading here", "deep/path/notes.md"));
    }

    // ── Sha256: stable, lowercase hex, content-sensitive ────────────────────────

    [Fact]
    public void Sha256_is_deterministic_and_changes_with_content()
    {
        var a = SourceScanner.Sha256("alpha");
        Assert.Equal(a, SourceScanner.Sha256("alpha"));
        Assert.NotEqual(a, SourceScanner.Sha256("beta"));
        Assert.Matches("^[0-9a-f]{64}$", a);
    }

    // ── DocId shape ─────────────────────────────────────────────────────────────

    [Fact]
    public void DocId_is_source_colon_path()
    {
        Assert.Equal("docs:dir/file.md", Document.MakeDocId("docs", "dir/file.md"));
    }
}
