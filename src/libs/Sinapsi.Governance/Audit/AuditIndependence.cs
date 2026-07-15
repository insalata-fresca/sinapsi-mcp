using Sinapsi.Governance.Accountability;

namespace Sinapsi.Governance.Audit;

/// <summary>
/// Enforces, at wire-up time, that an audit line is genuinely independent of the thing it
/// audits — the structural guard behind "not self-attestation" (docs/64 §2). An auditor
/// whose owner OR mechanism equals the evaluator's is a correlated fault masquerading as a
/// second opinion, and is rejected outright.
/// </summary>
public static class AuditIndependence
{
    /// <summary>
    /// Throw unless <paramref name="auditor"/> is independent of the first-line evaluator
    /// (<see cref="DeliveryEvaluatorAccountability.FirstLine"/>): a different owner AND a
    /// different mechanism.
    /// </summary>
    /// <exception cref="ArgumentException">the auditor shares the evaluator's owner or mechanism,
    /// or declares an empty owner/mechanism.</exception>
    public static void EnsureIndependentOfEvaluator(IIndependentAuditor auditor)
    {
        ArgumentNullException.ThrowIfNull(auditor);
        var evaluator = DeliveryEvaluatorAccountability.FirstLine;
        EnsureIndependent(auditor.Owner, auditor.Mechanism, evaluator.Named, evaluator.Mechanism);
    }

    /// <summary>
    /// The general check: an auditor (owner + mechanism) must differ from a subject's
    /// (owner + mechanism) on BOTH axes.
    /// </summary>
    public static void EnsureIndependent(string auditorOwner, string auditorMechanism, string subjectOwner, string subjectMechanism)
    {
        if (string.IsNullOrWhiteSpace(auditorOwner))
            throw new ArgumentException("an independent auditor must declare an owner", nameof(auditorOwner));
        if (string.IsNullOrWhiteSpace(auditorMechanism))
            throw new ArgumentException("an independent auditor must declare a mechanism", nameof(auditorMechanism));

        if (SameToken(auditorOwner, subjectOwner))
            throw new ArgumentException(
                $"audit line is NOT independent: it shares the evaluator's owner ('{auditorOwner}'). " +
                "The Third Line of Defense must be a different owner (docs/64 §2).", nameof(auditorOwner));

        if (SameToken(auditorMechanism, subjectMechanism))
            throw new ArgumentException(
                $"audit line is NOT independent: it shares the evaluator's mechanism ('{auditorMechanism}'). " +
                "Independence must come from a DIFFERENT mechanism — a second pass of the same one is a " +
                "correlated fault, not a vote (docs/64 §2).", nameof(auditorMechanism));
    }

    private static bool SameToken(string a, string b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}
