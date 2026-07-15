namespace Sinapsi.Governance.Audit;

/// <summary>
/// An independent auditor's finding over one verdict. <see cref="Concurs"/> = the auditor's
/// independent verdict matched the evaluator's. A dissent (especially where the evaluator
/// auto-proceeded but the auditor would escalate/deny) is the signal that should decay or
/// revoke trust and reach the accountable owner.
/// </summary>
public sealed record VerdictAuditRecord(
    string CorrelationId,
    bool Concurs,
    string IndependentVerdict,
    string AuditorOwner,
    string AuditorMechanism,
    string Note,
    DateTimeOffset AuditedAt);
