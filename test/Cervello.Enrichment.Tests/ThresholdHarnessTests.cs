using Cervello.Enrichment.Harness;
using Cervello.Enrichment.Policy;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// The threshold re-fit + held-out validation harnesses (spec <c>voiceprint-store</c> → "Threshold
/// 0.62 with a mandatory re-fit plan" / "Threshold is config, re-fittable" / "Auto-apply stays
/// gated until validated"; tasks E5 4.4/4.6). Proven on a SYNTHETIC labeled set whose pairwise
/// cosines we control via <see cref="TestVectors"/> — no personal audio, no biometric vectors.
/// </summary>
public sealed class ThresholdHarnessTests
{
    // Build a synthetic labeled set: two people, each with several tight same-speaker embeddings
    // (mutual cosine ≥ ~0.97), the two people well-separated (cross cosine ≤ ~0.3).
    private static List<LabeledSample> SyntheticSeparableSet()
    {
        var samples = new List<LabeledSample>();
        // person A: cluster around axis 0 (tilts toward axis 5 by tiny amounts → high mutual cosine)
        samples.Add(new LabeledSample("A", TestVectors.Axis(0)));
        samples.Add(new LabeledSample("A", TestVectors.TiltedFromAxis(0, 5, 0.98)));
        samples.Add(new LabeledSample("A", TestVectors.TiltedFromAxis(0, 6, 0.97)));
        // person B: cluster around axis 50 (orthogonal to A → low cross cosine)
        samples.Add(new LabeledSample("B", TestVectors.Axis(50)));
        samples.Add(new LabeledSample("B", TestVectors.TiltedFromAxis(50, 55, 0.98)));
        samples.Add(new LabeledSample("B", TestVectors.TiltedFromAxis(50, 56, 0.97)));
        return samples;
    }

    // ── 4.4 re-fit: distributions + recommended threshold + TPR/FPR ─────────────────────────────
    [Fact]
    public void Refit_recommends_a_separating_threshold_with_perfect_tpr_fpr_on_a_separable_set()
    {
        var report = ThresholdRefitHarness.Fit(SyntheticSeparableSet(), targetTpr: 0.95);

        // Same-speaker cosines are high, different-speaker low → clean separation.
        Assert.True(report.SameMean > report.DiffMean + 0.5,
            $"expected separated distributions, same={report.SameMean:0.###} diff={report.DiffMean:0.###}");
        Assert.True(report.MetTargetTpr);
        Assert.Equal(1.0, report.TprAtRecommended, 3); // all same-speaker pairs admitted
        Assert.Equal(0.0, report.FprAtRecommended, 3); // no different-speaker pair admitted
        // The recommended threshold sits ABOVE the different-speaker distribution, BELOW same-speaker.
        Assert.True(report.RecommendedThreshold > report.DiffMean);
        Assert.True(report.RecommendedThreshold <= report.SameMean + 1e-9);
    }

    [Fact]
    public void Refit_is_deterministic()
    {
        var a = ThresholdRefitHarness.Fit(SyntheticSeparableSet());
        var b = ThresholdRefitHarness.Fit(SyntheticSeparableSet());
        Assert.Equal(a.RecommendedThreshold, b.RecommendedThreshold, 9);
        Assert.Equal(a.TprAtRecommended, b.TprAtRecommended, 9);
        Assert.Equal(a.FprAtRecommended, b.FprAtRecommended, 9);
    }

    [Fact]
    public void Refit_output_feeds_DecisionBands_by_config_with_no_code_change()
    {
        // "Threshold is config, re-fittable": the fitted value constructs a DecisionBands with no
        // engine change. The recommended value must be a legal auto-band (> reject, < 1).
        var report = ThresholdRefitHarness.Fit(SyntheticSeparableSet());
        var refitted = new DecisionBands(autoBand: report.RecommendedThreshold, rejectBand: DecisionBands.DefaultRejectBand);
        Assert.Equal(report.RecommendedThreshold, refitted.AutoBand, 9);
        Assert.True(refitted.IsAuto(report.RecommendedThreshold));
    }

    // ── 4.6 held-out validation gate: PASS → graded, FAIL → escalate-only ───────────────────────
    [Fact]
    public void Held_out_validation_passing_the_bar_enables_graded_auto_apply()
    {
        var report = HeldOutValidationHarness.Validate(
            candidateThreshold: 0.62, heldOut: SyntheticSeparableSet(), bar: new AcceptanceBar(0.95, 0.02));

        Assert.True(report.Passed);
        Assert.Equal(PolicyPhase.GradedAutoApply, report.RecommendedPhase);
        Assert.Equal(1.0, report.Tpr, 3);
        Assert.Equal(0.0, report.Fpr, 3);
    }

    [Fact]
    public void Held_out_validation_failing_the_bar_stays_escalate_only()
    {
        // A degenerate held-out set where same and different overlap (people not separable at 0.62):
        // person A and person B both near axis 0 → cross-speaker cosine is HIGH → FPR blows the bar.
        var overlap = new List<LabeledSample>
        {
            new("A", TestVectors.Axis(0)),
            new("A", TestVectors.TiltedFromAxis(0, 5, 0.99)),
            new("B", TestVectors.TiltedFromAxis(0, 6, 0.99)), // B sits right on top of A
            new("B", TestVectors.TiltedFromAxis(0, 7, 0.99)),
        };

        var report = HeldOutValidationHarness.Validate(0.62, overlap, new AcceptanceBar(0.95, 0.02));

        Assert.False(report.Passed);
        Assert.Equal(PolicyPhase.EscalateOnly, report.RecommendedPhase); // gate holds
        Assert.True(report.Fpr > 0.02, $"expected FPR over the bar, got {report.Fpr:0.###}");
    }

    [Fact]
    public void The_acceptance_bar_is_a_parameter_set_with_the_operator()
    {
        var set = SyntheticSeparableSet();
        // A strict bar the operator could set; the separable set still passes it.
        var strict = HeldOutValidationHarness.Validate(0.62, set, new AcceptanceBar(minTpr: 0.99, maxFpr: 0.0));
        Assert.True(strict.Passed);
        // The default bar is available + conservative.
        Assert.Equal(0.95, AcceptanceBar.Default.MinTpr, 3);
        Assert.Equal(0.02, AcceptanceBar.Default.MaxFpr, 3);
    }
}
