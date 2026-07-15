using Xunit;

namespace Sinapsi.DeliveryEvaluator.Tests;

/// <summary>The load-bearing guarantee (home-server <c>docs/64 §2</c>, <c>docs/65</c> principle 5):
/// a trust/security-plane change is NEVER auto-allowed by the evaluator's own judgment — the verdict
/// is deterministic-escalate or deny.</summary>
public class TrustPlaneNeverAllowTests
{
    public static IEnumerable<object[]> TrustPlaneChanges() => new[]
    {
        new object[] { ChangeSet.Of(FileChange.Added_("policies/openfga/tuples.json", "user:agent viewer tool:gdrive")) },
        new object[] { ChangeSet.Of(FileChange.Added_("infra/secrets/service.key", "-----BEGIN PRIVATE KEY-----")) },
        new object[] { ChangeSet.Of(FileChange.Added_("gateway/agentgateway/config.yaml", "mcpAuthorization: rules")) },
        new object[] { ChangeSet.Of(FileChange.Added_("nats/accounts.conf", "auth-callout permissions")) },
        new object[] { ChangeSet.Of(FileChange.Added_("src/Authz/CommandCapabilities.cs", "reclassify verb as read")) },
        new object[] { ChangeSet.Of(FileChange.Added_("src/log.cs", "add a new outbound HTTP call to an external host")) },
        new object[] { ChangeSet.Of(FileChange.Added_("cfg/env", "SHADOW=false")) },
    };

    [Theory]
    [MemberData(nameof(TrustPlaneChanges))]
    public void a_trust_plane_change_is_never_allowed(ChangeSet change)
    {
        var v = DeterministicRiskClassifier.Classify(change);
        Assert.NotEqual(Verdict.Allow, v.Verdict);
        Assert.Contains(v.Verdict, new[] { Verdict.RequiresApproval, Verdict.Deny });
    }

    [Fact]
    public void an_openfga_tuple_buried_in_400_lines_of_docs_still_escalates()
    {
        // docs/65 principle 4: tier = MAX over surfaces. One tuple makes the whole change trust-plane.
        var files = new List<FileChange> { FileChange.Added_("policies/openfga/tuples.json", "grant a broad relation") };
        for (var i = 0; i < 400; i++) files.Add(FileChange.Added_($"docs/page{i}.md", "prose"));
        var v = DeterministicRiskClassifier.Classify(ChangeSet.Of(files.ToArray()));
        Assert.Equal(RiskTier.TrustPlane, v.Tier);
        Assert.NotEqual(Verdict.Allow, v.Verdict);
    }

    [Fact]
    public void a_shadow_to_enforce_flip_is_a_deterministic_deny()
    {
        var v = DeterministicRiskClassifier.Classify(
            ChangeSet.Of(FileChange.Added_("deploy/authz.env", "SHADOW=false  # enforce the Q1 layer")));
        Assert.Equal(Verdict.Deny, v.Verdict);
        Assert.Equal(Confidence.High, v.Confidence);
    }

    [Fact]
    public void the_envelope_carries_the_shared_verdict_vocabulary_and_effect_classification()
    {
        var v = DeterministicRiskClassifier.Classify(
            ChangeSet.Of(new[] { FileChange.Added_("policies/openfga/tuples.json", "grant") },
                UntrustedChangeMetadata.None, correlationId: "trace-123"));
        var data = DeliveryVerdictEnvelope.ToEnvelopeData(v, "trace-123");
        Assert.Equal("requiresApproval", (string?)data["verdict"]);
        Assert.Equal("trace-123", (string?)data["correlation_id"]);
        Assert.Equal(true, (bool?)data["touched_trust_plane"]);
        // The subject is a verdict FACT (never an act trigger).
        var subject = DeliveryVerdictEnvelope.SubjectFor(v);
        Assert.True(Sinapsi.Nats.EventPlane.EventPlaneChannels.IsVerdictFactSubject(subject));
        Assert.False(Sinapsi.Nats.EventPlane.EventPlaneChannels.IsActCommandSubject(subject));
    }
}
