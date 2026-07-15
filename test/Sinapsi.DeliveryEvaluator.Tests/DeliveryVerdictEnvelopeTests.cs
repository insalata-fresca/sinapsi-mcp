using System.Text.Json.Nodes;
using Sinapsi.Nats.EventPlane;
using Xunit;

namespace Sinapsi.DeliveryEvaluator.Tests;

public class DeliveryVerdictEnvelopeTests
{
    [Fact]
    public void the_envelope_reuses_the_published_language_verdict_vocabulary()
    {
        foreach (var verdict in new[] { Verdict.Allow, Verdict.RequiresApproval, Verdict.Deny })
        {
            var rv = new RiskVerdict(verdict, RiskTier.ApplicationCode, Confidence.Medium, "r",
                Array.Empty<TrustSurface>(), Array.Empty<RiskSignal>());
            var data = DeliveryVerdictEnvelope.ToEnvelopeData(rv);
            Assert.Contains((string)data["verdict"]!, DecisionEnvelopeContract.Verdicts);
        }
    }

    [Fact]
    public void a_verdict_fact_subject_is_never_an_act_command_subject()
    {
        var rv = new RiskVerdict(Verdict.RequiresApproval, RiskTier.TrustPlane, Confidence.High, "r",
            new[] { TrustSurface.OpenFgaRelation }, Array.Empty<RiskSignal>());
        var subject = DeliveryVerdictEnvelope.SubjectFor(rv);

        Assert.True(EventPlaneChannels.IsVerdictFactSubject(subject));
        // Guard: attempting to dispatch this fact subject as an act command must be rejected.
        Assert.Throws<ArgumentException>(() => EventPlaneChannels.EnsureNotFactTriggered(subject));
    }

    [Fact]
    public void an_unparseable_change_routes_to_the_dead_letter_root()
    {
        var v = DeterministicRiskClassifier.Classify(ChangeSet.Of());
        var subject = DeliveryVerdictEnvelope.SubjectFor(v);
        Assert.True(EventPlaneChannels.IsDeadLetterSubject(subject));
    }

    [Fact]
    public void the_envelope_data_carries_the_effect_classification()
    {
        var v = DeterministicRiskClassifier.Classify(
            ChangeSet.Of(FileChange.Added_("policies/openfga/tuples.json", "grant")));
        JsonObject data = DeliveryVerdictEnvelope.ToEnvelopeData(v, "cid-1");

        Assert.Equal("delivery-evaluator", (string?)data["layer"]);
        Assert.Equal("cid-1", (string?)data["correlation_id"]);
        Assert.Equal("TrustPlane", (string?)data["tier"]);
        Assert.Contains("OpenFgaRelation", data["surfaces"]!.AsArray().Select(n => (string?)n));
    }
}
