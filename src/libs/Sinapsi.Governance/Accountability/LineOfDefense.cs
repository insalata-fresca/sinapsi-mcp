namespace Sinapsi.Governance.Accountability;

/// <summary>
/// The Three Lines of Defense model (home-server <c>docs/64 §3</c>) applied to delivery
/// verdicts: independence must come from separation of roles, not self-attestation.
/// </summary>
public enum LineOfDefense
{
    /// <summary>First line — the operational owner that MAKES the verdict: the C1 delivery
    /// evaluator (a deterministic classifier). It owns day-to-day correctness.</summary>
    First = 1,

    /// <summary>Second line — risk oversight that MONITORS the first line without doing its
    /// work: this governance layer (D1) — the trust ledger, the escalation SLO, the
    /// retrospective inspection. It owns "is the evaluator still trustworthy over time".</summary>
    Second = 2,

    /// <summary>Third line — INDEPENDENT audit, a different mechanism and a different owner,
    /// with authority to dissent and to revoke. Not the evaluator grading itself.</summary>
    Third = 3,
}
