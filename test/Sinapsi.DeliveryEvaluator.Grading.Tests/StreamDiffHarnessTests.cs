using System.Text.Json.Nodes;
using Sinapsi.DeliveryEvaluator;
using Sinapsi.DeliveryEvaluator.Grading;
using Xunit;

namespace Sinapsi.DeliveryEvaluator.Grading.Tests;

/// <summary>
/// The shadow-vs-would-enforce stream-diff harness: it must AUTO-FAIL promotion on any deviation
/// or unverifiable record, and reproduce a faithful shadow stream cleanly (home-server
/// <c>docs/64 §4</c>).
/// </summary>
public class StreamDiffHarnessTests
{
    // A trust-plane change: the evaluator escalates it deterministically.
    private const string TrustPlaneChange = "adds a new OpenFGA relation to policies/openfga/tuples.json";
    // A docs-only change: the evaluator allows it.
    private const string DocsChange = "Fix a typo in docs/00-overview.md; no other files touched.";

    [Fact]
    public void a_faithful_shadow_stream_reproduces_exactly_and_passes()
    {
        // Shadow verdicts equal to what the evaluator produces now → all match, gate passes.
        var stream = new[]
        {
            new ShadowDecision("c1", Verdict.RequiresApproval, TrustPlaneChange),
            new ShadowDecision("c2", Verdict.Allow, DocsChange),
        };
        var report = StreamDiffHarness.Diff(stream);
        Assert.Equal(2, report.Matches);
        Assert.False(report.PromotionAutoFailed);
        Assert.Equal("PASSED", report.GateWord);
    }

    [Fact]
    public void an_unsafe_more_permissive_deviation_auto_fails_promotion()
    {
        // Shadow held a trust-plane change (requiresApproval) but the current evaluator would ALLOW
        // the recorded verdict is stricter than would-enforce → MorePermissive regression.
        // We simulate drift by recording an allow-labelled input under a shadow 'deny'.
        var stream = new[] { new ShadowDecision("c3", Verdict.Deny, DocsChange) };
        var report = StreamDiffHarness.Diff(stream);
        Assert.True(report.PromotionAutoFailed);
        Assert.Single(report.Deviations);
        Assert.Single(report.UnsafeDeviations); // would-enforce=allow is more permissive than shadow=deny
        Assert.Equal(DeviationDirection.MorePermissive, report.UnsafeDeviations[0].Direction);
    }

    [Fact]
    public void a_stricter_deviation_still_fails_the_reproduction_gate_but_is_not_unsafe()
    {
        // Shadow allowed a trust-plane change; would-enforce escalates it → stricter (safe direction)
        // but still a deviation that fails a strict reproduction gate.
        var stream = new[] { new ShadowDecision("c4", Verdict.Allow, TrustPlaneChange) };
        var report = StreamDiffHarness.Diff(stream);
        Assert.True(report.PromotionAutoFailed);
        Assert.Single(report.Deviations);
        Assert.Empty(report.UnsafeDeviations);
        Assert.Equal(DeviationDirection.Stricter, report.Deviations[0].Direction);
    }

    [Fact]
    public void an_unverifiable_record_with_no_change_blocks_promotion_fail_safe()
    {
        var stream = new[] { new ShadowDecision("c5", Verdict.Allow, DiffSummary: null) };
        var report = StreamDiffHarness.Diff(stream);
        Assert.True(report.PromotionAutoFailed);
        Assert.Single(report.Unverifiable);
    }

    [Fact]
    public void parses_a_shadow_decision_from_a_real_delivery_envelope_payload()
    {
        // Build the envelope exactly as the evaluator emits it, then round-trip through the parser.
        var change = CorpusScenarioAdapter.ToChangeSet(TrustPlaneChange, correlationId: "corr-9");
        var verdict = DeterministicRiskClassifier.Classify(change);
        var data = DeliveryVerdictEnvelope.ToEnvelopeData(verdict, correlationId: "corr-9");
        // The live-wire follow-on: the emitter must add diff_summary so the record is recomputable.
        data["diff_summary"] = TrustPlaneChange;

        var decision = ShadowDecision.FromEnvelopeData(data);
        Assert.Equal("corr-9", decision.CorrelationId);
        Assert.Equal(Verdict.RequiresApproval, decision.ShadowVerdict);
        Assert.True(decision.CanRecompute);

        var report = StreamDiffHarness.Diff(new[] { decision });
        Assert.False(report.PromotionAutoFailed); // faithful → reproduces
    }

    [Fact]
    public void jsonl_source_reads_a_captured_shadow_stream()
    {
        string Line(string id, string verdict, string diff) =>
            new JsonObject { ["correlation_id"] = id, ["verdict"] = verdict, ["diff_summary"] = diff }.ToJsonString();

        var src = new JsonlShadowDecisionSource(new[]
        {
            Line("a", "requiresApproval", TrustPlaneChange),
            Line("b", "allow", DocsChange),
        });
        var report = StreamDiffHarness.Diff(src.Read());
        Assert.Equal(2, report.Total);
        Assert.False(report.PromotionAutoFailed);
    }
}
