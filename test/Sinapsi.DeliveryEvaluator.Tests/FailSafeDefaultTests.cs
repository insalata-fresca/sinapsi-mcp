using Xunit;

namespace Sinapsi.DeliveryEvaluator.Tests;

public class FailSafeDefaultTests
{
    [Fact]
    public void null_change_escalates_and_deadletters()
    {
        var v = DeterministicRiskClassifier.Classify(null);
        Assert.Equal(Verdict.RequiresApproval, v.Verdict);
        Assert.True(v.Unparseable);
        Assert.StartsWith("delivery.dlq.", DeliveryVerdictEnvelope.SubjectFor(v));
    }

    [Fact]
    public void empty_change_set_escalates()
    {
        var v = DeterministicRiskClassifier.Classify(ChangeSet.Of());
        Assert.Equal(Verdict.RequiresApproval, v.Verdict);
        Assert.True(v.Unparseable);
    }

    [Fact]
    public void an_unrecognised_surface_with_no_signal_escalates_never_allows()
    {
        // A pathless, signal-free change: the evaluator cannot positively clear it → fail-safe.
        var change = ChangeSet.Of(new FileChange("", ChangeKind.Modified, new[] { "some inscrutable prose" }, Array.Empty<string>()));
        var v = DeterministicRiskClassifier.Classify(change);
        Assert.Equal(Verdict.RequiresApproval, v.Verdict);
        Assert.NotEqual(Verdict.Allow, v.Verdict);
        Assert.Equal(Confidence.Low, v.Confidence);
    }

    [Fact]
    public void allow_requires_a_positive_clearance_not_a_fall_through()
    {
        // Application code with NO deterministic-CI-green signal must not auto-allow.
        var change = ChangeSet.Of(FileChange.Added_("src/Svc/Handler.cs", "public int Add(int a,int b)=>a+b;"));
        var v = DeterministicRiskClassifier.Classify(change);
        Assert.Equal(Verdict.RequiresApproval, v.Verdict);
    }

    [Fact]
    public void docs_only_clean_change_is_allowed()
    {
        var change = ChangeSet.Of(FileChange.Added_("docs/00-overview.md", "Fix a typo and reword a sentence."));
        var v = DeterministicRiskClassifier.Classify(change);
        Assert.Equal(Verdict.Allow, v.Verdict);
        Assert.Equal(RiskTier.DocsOnly, v.Tier);
    }
}
