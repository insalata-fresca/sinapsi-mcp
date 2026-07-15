namespace Sinapsi.Governance;

/// <summary>
/// The graduated authority a change-class currently holds — the DATA the evaluator /
/// pipeline reads to decide whether it may act on its own judgment or must escalate.
/// Authority is <b>earned</b> only by proven shadow reliability and is instantly
/// <b>revocable</b>; only <see cref="Earned"/> unlocks auto-proceed on the green-light
/// path (home-server <c>docs/62 §1</c>). Everything below <see cref="Earned"/> means
/// "route to the operator" — the fail-safe default (docs/64 §3).
/// </summary>
public enum AutoProceedAuthority
{
    /// <summary>Kill switch tripped. Trust was revoked out-of-band; every decision in
    /// this class escalates, regardless of prior score. Overrides the starvation floor.</summary>
    Revoked = 0,

    /// <summary>The conservative baseline — the floor a class decays toward. Not (yet)
    /// trusted to auto-proceed; escalate. Reachable after a miss, or as the cold-start state.</summary>
    Baseline = 1,

    /// <summary>Climbing: reliability is accruing but has not cleared the earned bar
    /// (score and/or consecutive-confirmation gate not yet met). Still escalates.</summary>
    Probationary = 2,

    /// <summary>Auto-proceed authorized. The pipeline may merge/deploy this class on the
    /// green path without an operator pause. The only band for which
    /// <see cref="TrustLedgerEntry.MayAutoProceed"/> is true.</summary>
    Earned = 3,
}
