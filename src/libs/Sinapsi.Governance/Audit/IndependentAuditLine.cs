using Sinapsi.Governance.Events;

namespace Sinapsi.Governance.Audit;

/// <summary>
/// The wired independent audit line: it holds a <see cref="IIndependentAuditor"/> whose
/// independence from the evaluator is verified at construction (so a self-attesting auditor
/// can never be installed), runs it over the verdicts it is handed, emits an
/// <c>audit</c> governance fact per finding, and counts dissents (the signal the operator
/// and the trust ledger consume). Pure apart from the injected sink.
/// </summary>
public sealed class IndependentAuditLine
{
    private readonly IIndependentAuditor _auditor;
    private readonly IGovernanceEventSink _sink;

    public IndependentAuditLine(IIndependentAuditor auditor, IGovernanceEventSink? sink = null)
    {
        AuditIndependence.EnsureIndependentOfEvaluator(auditor);
        _auditor = auditor;
        _sink = sink ?? NullGovernanceEventSink.Instance;
    }

    /// <summary>How many audited verdicts the independent line disagreed with.</summary>
    public int DissentCount { get; private set; }

    /// <summary>Audit one verdict; emit the finding as a fact; track dissent.</summary>
    public VerdictAuditRecord Audit(EvaluatorVerdictReview review)
    {
        var record = _auditor.Audit(review);
        if (!record.Concurs) DissentCount++;
        _sink.Emit(new GovernanceEvent(
            Subject: GovernanceChannels.Audit(record.Concurs),
            Kind: "audit",
            Summary: $"audit[{review.ChangeClass}] corr={review.CorrelationId} " +
                     $"evaluator={review.EvaluatorVerdict} independent={record.IndependentVerdict} " +
                     $"concurs={record.Concurs} by={record.AuditorOwner}",
            At: record.AuditedAt));
        return record;
    }
}
