namespace Sinapsi.Governance;

/// <summary>
/// An immutable snapshot of one change-class's trust state. This is the record the
/// evaluator / pipeline reads: <see cref="Authority"/> (and its convenience
/// <see cref="MayAutoProceed"/>) is the load-bearing datum — everything else is the
/// audit trail of how it got there.
/// </summary>
public sealed record TrustLedgerEntry(
    ChangeClass ChangeClass,
    double Score,
    int ConsecutiveReliable,
    AutoProceedAuthority Authority,
    bool Revoked,
    string? RevokedReason,
    ShadowOutcome? LastOutcome,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// The single datum the pipeline acts on: may this change-class auto-proceed on the
    /// green path right now? True ONLY at <see cref="AutoProceedAuthority.Earned"/>.
    /// </summary>
    public bool MayAutoProceed => Authority == AutoProceedAuthority.Earned;

    /// <summary>The cold-start entry for a class: at the floor, no history, escalate-by-default.</summary>
    public static TrustLedgerEntry Baseline(ChangeClass changeClass, double floor, DateTimeOffset at) => new(
        ChangeClass: changeClass,
        Score: floor,
        ConsecutiveReliable: 0,
        Authority: AutoProceedAuthority.Baseline,
        Revoked: false,
        RevokedReason: null,
        LastOutcome: null,
        UpdatedAt: at);
}
