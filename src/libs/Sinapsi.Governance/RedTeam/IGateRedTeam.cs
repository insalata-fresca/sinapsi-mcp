namespace Sinapsi.Governance.RedTeam;

/// <summary>
/// The continuous red-team hook over the gate (docs/64 §3). An implementation runs its
/// probe corpus against a live gate on a schedule — the gate is treated as adversarially
/// attacked, not assumed correct — and reports any breach. The gate under test is supplied
/// as a delegate returning whether it would AUTO-ALLOW a given probe, so the red team never
/// needs the gate's internals (and can drive the C1 evaluator, a future LLM judge, or a
/// stubbed gate interchangeably).
/// </summary>
public interface IGateRedTeam
{
    /// <summary>The adversarial probe corpus.</summary>
    IReadOnlyList<AdversarialProbe> Probes { get; }

    /// <summary>Run every probe through <paramref name="gateAutoAllows"/> and report findings.</summary>
    IReadOnlyList<RedTeamFinding> Run(Func<AdversarialProbe, bool> gateAutoAllows);
}
