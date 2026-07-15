namespace Sinapsi.Governance.Audit;

/// <summary>
/// The independent audit line over the evaluator's verdicts — the Third Line of Defense.
/// An implementation MUST be a <b>different mechanism</b> and a <b>different owner</b> from
/// the evaluator it audits (docs/64 §2: a second LLM pass is not an independent vote — a
/// correlated fault; independence must come from a different mechanism). The
/// <see cref="Owner"/> / <see cref="Mechanism"/> are declared so
/// <see cref="AuditIndependence"/> can enforce the separation at wire-up time.
/// </summary>
public interface IIndependentAuditor
{
    /// <summary>The auditor's owner — must differ from the audited evaluator's owner.</summary>
    string Owner { get; }

    /// <summary>How this auditor reaches its verdict — must differ from the evaluator's mechanism.</summary>
    string Mechanism { get; }

    /// <summary>Form an independent verdict over the change and compare it to the evaluator's.</summary>
    VerdictAuditRecord Audit(EvaluatorVerdictReview review);
}
