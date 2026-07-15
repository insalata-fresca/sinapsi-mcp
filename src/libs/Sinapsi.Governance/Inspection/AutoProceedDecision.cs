namespace Sinapsi.Governance.Inspection;

/// <summary>
/// A record of one delivery decision, kept so the governance layer can sample it for human
/// review after the fact ("inspected trust"). Auto-proceed decisions are the ones that
/// carry risk — they happened without an operator in the loop — so they are the primary
/// inspection population.
/// </summary>
public sealed record AutoProceedDecision(
    string CorrelationId,
    ChangeClass ChangeClass,
    string Verdict,
    bool AutoProceeded,
    DateTimeOffset DecidedAt);
