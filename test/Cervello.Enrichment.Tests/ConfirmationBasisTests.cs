using Cervello.Enrichment.Domain;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// The confirmation-basis grammar every applied attribution must carry (lint R9; SCHEMAS §6;
/// DESIGN §5.1/§10.2). Proves the two accepted forms and that a bare voice-match / any other
/// scheme is rejected — the "assistive-only until a basis" invariant, mechanized.
/// </summary>
public sealed class ConfirmationBasisTests
{
    [Fact]
    public void Auto_basis_formats_as_auto_rule_at_version()
    {
        var b = ConfirmationBasis.Auto("v1");
        Assert.Equal(ConfirmationBasisKind.Auto, b.Kind);
        Assert.Equal("auto://voice-match@v1", b.Id);
        Assert.Equal("voice-match", b.Rule);
        Assert.Equal("v1", b.Version);
    }

    [Fact]
    public void Human_basis_formats_as_human_answer_id()
    {
        var b = ConfirmationBasis.Human("op_42");
        Assert.Equal(ConfirmationBasisKind.Human, b.Kind);
        Assert.Equal("human://op_42", b.Id);
    }

    [Theory] // scenario: Auto-applied / operator-answered attribution has a valid basis (R9 accepts)
    [InlineData("auto://voice-match@v1", ConfirmationBasisKind.Auto)]
    [InlineData("auto://voice-match@v2.1", ConfirmationBasisKind.Auto)]
    [InlineData("human://op_1", ConfirmationBasisKind.Human)]
    public void Valid_basis_ids_parse(string id, ConfirmationBasisKind kind)
    {
        Assert.True(ConfirmationBasis.TryParse(id, out var b));
        Assert.Equal(kind, b!.Kind);
        Assert.Equal(id, b.Id);
    }

    [Theory] // scenario: Basis-less attribution is rejected — a bare voice-match / bad scheme fails R9
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("guilhem said")]          // a bare "X said" with no basis
    [InlineData("voice-match@v1")]        // no scheme
    [InlineData("auto://voice-match")]    // no version
    [InlineData("auto://@v1")]            // no rule
    [InlineData("http://evil")]           // wrong scheme
    [InlineData("rec://r1#s1")]           // a source ref is not a basis
    public void Invalid_or_missing_basis_ids_are_rejected(string? id)
    {
        Assert.False(ConfirmationBasis.TryParse(id, out var b));
        Assert.Null(b);
    }

    [Fact] // an applied verdict is structurally forced to carry a basis (cannot construct without one)
    public void An_applied_verdict_cannot_be_constructed_without_a_basis()
    {
        Assert.Throws<ArgumentNullException>(() =>
            AttributionVerdict.AutoApplied("s1", "guilhem", 0.8, "rec://r1#s1", basis: null!));
        Assert.Throws<ArgumentException>(() =>
            AttributionVerdict.AutoApplied("s1", "guilhem", 0.8, sourceRef: "", ConfirmationBasis.Auto("v1")));
    }

    [Fact] // withheld / omitted verdicts carry NO basis (assistive-only until a confirmation)
    public void Open_point_and_omitted_carry_no_basis()
    {
        Assert.Null(AttributionVerdict.OpenPoint("s1", 0.55, "review").Basis);
        Assert.Null(AttributionVerdict.Omitted("s1", "unidentified").Basis);
    }
}
