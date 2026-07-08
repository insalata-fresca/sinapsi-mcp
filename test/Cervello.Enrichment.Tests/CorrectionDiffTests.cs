using Cervello.Enrichment.Domain;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// The <see cref="CorrectionDiff"/> value object enforces the no-fabrication floor in its
/// constructor (DESIGN §5.1): an unbacked diff (no evidence ref) cannot be constructed, and a
/// "correction" that changes nothing is not a correction. This makes the "never invent" floor a
/// TYPE property, not just stage logic.
/// </summary>
public sealed class CorrectionDiffTests
{
    private static readonly TextSpan Span = new(0, 5);

    [Fact]
    public void A_diff_requires_an_evidence_ref()
    {
        Assert.Throws<ArgumentException>(() =>
            new CorrectionDiff(Span, "Gilan", "Guilhem", CorrectionKind.Name, 0.9, evidenceRef: ""));
        Assert.Throws<ArgumentException>(() =>
            new CorrectionDiff(Span, "Gilan", "Guilhem", CorrectionKind.Name, 0.9, evidenceRef: "  "));
    }

    [Fact]
    public void A_diff_must_change_the_text()
    {
        Assert.Throws<ArgumentException>(() =>
            new CorrectionDiff(Span, "same", "same", CorrectionKind.Term, 0.9, "glossary://same"));
    }

    [Fact]
    public void A_backed_diff_carries_before_after_and_evidence()
    {
        var diff = new CorrectionDiff(Span, "Gilan", "Guilhem", CorrectionKind.Name, 0.9, "[[guilhem]]");
        Assert.Equal("Gilan", diff.Before);
        Assert.Equal("Guilhem", diff.After);
        Assert.Equal("[[guilhem]]", diff.EvidenceRef);
    }

    [Fact]
    public void Confidence_out_of_range_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CorrectionDiff(Span, "a", "b", CorrectionKind.Term, 1.5, "glossary://a"));
    }

    [Fact]
    public void A_span_must_be_non_empty_and_ordered()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextSpan(5, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextSpan(5, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextSpan(-1, 2));
    }
}
