using Cervello.Enrichment.Domain;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// Google <c>[Speaker N]</c> labels as an OPTIONAL confirming prior (spec <c>enrichment-linking</c>
/// → "Google labels are an optional confirming signal only"). Absence never degrades enrichment;
/// presence only raises the confirming weight, never replaces the audio diarizer's segments.
/// </summary>
public sealed class GoogleLabelPriorTests
{
    // ── Scenario: Missing Google labels do not block enrichment ─────────────────────────────────
    [Fact]
    public void Missing_labels_are_a_noop_never_degrade()
    {
        var prior = GoogleLabelPrior.None;
        Assert.False(prior.HasLabels);
        // No label → no confirming bump for any resolution; nothing is subtracted or blocked.
        Assert.Equal(0.0, prior.ConfirmingBump("[Speaker 1]", "guilhem"));
    }

    // ── Scenario: Present Google labels are only a confirming prior ─────────────────────────────
    [Fact]
    public void Present_agreeing_label_raises_the_confirming_weight_only()
    {
        var prior = GoogleLabelPrior.From(new Dictionary<string, string>
        {
            ["[Speaker 1]"] = "guilhem",
            ["[Speaker 2]"] = "stefano",
        });
        Assert.True(prior.HasLabels);

        // Agreeing → a small positive bump (never decisive).
        Assert.Equal(GoogleLabelPrior.ConfirmingWeight, prior.ConfirmingBump("[Speaker 1]", "guilhem"));
        Assert.True(GoogleLabelPrior.ConfirmingWeight < 0.1);

        // Disagreeing / absent label → no bump, but no penalty either.
        Assert.Equal(0.0, prior.ConfirmingBump("[Speaker 1]", "stefano"));
        Assert.Equal(0.0, prior.ConfirmingBump("[Speaker 9]", "guilhem"));
    }
}
