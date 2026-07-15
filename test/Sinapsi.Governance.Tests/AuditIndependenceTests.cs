using Sinapsi.Governance;
using Sinapsi.Governance.Accountability;
using Sinapsi.Governance.Audit;
using Sinapsi.Governance.Events;
using Xunit;

namespace Sinapsi.Governance.Tests;

public sealed class AuditIndependenceTests
{
    // An auditor that is genuinely independent (different owner + different mechanism).
    private sealed class DeterministicPathAuditor : IIndependentAuditor
    {
        public string Owner => "Second-opinion static path classifier (governance-owned)";
        public string Mechanism => "independent path all/deny-list check (not the evaluator's classifier)";
        public VerdictAuditRecord Audit(EvaluatorVerdictReview r) => new(
            r.CorrelationId,
            Concurs: r.ChangeClass != ChangeClass.TrustPlane || !r.AutoProceeded,
            IndependentVerdict: r.ChangeClass == ChangeClass.TrustPlane ? "requiresApproval" : r.EvaluatorVerdict,
            AuditorOwner: Owner, AuditorMechanism: Mechanism,
            Note: "independent re-classification", AuditedAt: DateTimeOffset.UnixEpoch);
    }

    // A "second LLM pass" style auditor that shares the evaluator's mechanism — a correlated fault.
    private sealed class SameMechanismAuditor : IIndependentAuditor
    {
        public string Owner => "someone else";
        public string Mechanism => DeliveryEvaluatorAccountability.FirstLine.Mechanism; // SAME
        public VerdictAuditRecord Audit(EvaluatorVerdictReview r) => throw new NotImplementedException();
    }

    [Fact]
    public void NamedAccountableOwner_IsTheOperator_ThirdLine()
    {
        var owner = DeliveryEvaluatorAccountability.AccountableOwner;
        Assert.Equal(LineOfDefense.Third, owner.Line);
        Assert.Contains("Operator", owner.Named);
        Assert.Equal(3, DeliveryEvaluatorAccountability.Lines.Count);
    }

    [Fact]
    public void IndependentAuditor_IsAccepted()
    {
        var line = new IndependentAuditLine(new DeterministicPathAuditor());
        Assert.Equal(0, line.DissentCount);
    }

    [Fact]
    public void AuditorSharingEvaluatorMechanism_IsRejected_NotAnIndependentVote()
    {
        Assert.Throws<ArgumentException>(() => new IndependentAuditLine(new SameMechanismAuditor()));
    }

    [Fact]
    public void DissentOnTrustPlaneAutoProceed_IsCountedAndEmitted()
    {
        var sink = new RecordingGovernanceEventSink();
        var line = new IndependentAuditLine(new DeterministicPathAuditor(), sink);

        var review = new EvaluatorVerdictReview("corr-1", ChangeClass.TrustPlane,
            EvaluatorVerdict: "allow", AutoProceeded: true, ChangeEffectSummary: "adds an OpenFGA tuple");
        var record = line.Audit(review);

        Assert.False(record.Concurs);
        Assert.Equal(1, line.DissentCount);
        var ev = Assert.Single(sink.Events);
        Assert.Equal(GovernanceChannels.Audit(concurred: false), ev.Subject);
    }
}
