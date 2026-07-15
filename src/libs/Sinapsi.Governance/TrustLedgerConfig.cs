namespace Sinapsi.Governance;

/// <summary>
/// The tunables that shape graduated + revocable trust. All are policy DATA — no
/// behaviour is hard-coded — so the operator can retune the ratchet/decay without a
/// code change. The invariants the defaults encode:
/// <list type="bullet">
///   <item><b>Ratchet up only on proven reliability</b> — a class reaches
///     <see cref="AutoProceedAuthority.Earned"/> only when its score clears
///     <see cref="EarnedThreshold"/> AND it has strung together
///     <see cref="RatchetConfirmations"/> consecutive <see cref="ShadowOutcome.Reliable"/>
///     outcomes. One reliable run is never enough.</item>
///   <item><b>Decay toward baseline on any miss</b> — a single <see cref="ShadowOutcome.Miss"/>
///     multiplies the score by <see cref="DecayFactor"/> and zeroes the streak.</item>
///   <item><b>Floor, not starvation</b> — decay never drives the score below
///     <see cref="Floor"/> (&gt; 0), so a class is knocked back to escalate-by-default,
///     not punished into a permanent hole ("agent starvation", docs/64 §3).</item>
///   <item><b>Trust-plane hard cap</b> — <see cref="Ceilings"/> caps
///     <see cref="ChangeClass.TrustPlane"/> (and <see cref="ChangeClass.Unknown"/>) BELOW
///     <see cref="EarnedThreshold"/>, so no streak of reliability can ever make the trust
///     plane self-clear to auto-allow (docs/64 §2, docs/65 principle 5).</item>
/// </list>
/// </summary>
public sealed record TrustLedgerConfig(
    double RatchetStep,
    double DecayFactor,
    double Floor,
    double ProbationThreshold,
    double EarnedThreshold,
    int RatchetConfirmations,
    IReadOnlyDictionary<ChangeClass, double> Ceilings)
{
    /// <summary>The starvation-floor score every class (except a revoked one) is clamped to.</summary>
    public const double DefaultFloor = 0.10;

    /// <summary>
    /// The canon-aligned defaults. Trust-plane + unknown are capped at
    /// <see cref="ProbationThreshold"/> so they top out at
    /// <see cref="AutoProceedAuthority.Probationary"/> and NEVER reach
    /// <see cref="AutoProceedAuthority.Earned"/>.
    /// </summary>
    public static TrustLedgerConfig Default { get; } = new(
        RatchetStep: 0.15,
        DecayFactor: 0.5,
        Floor: DefaultFloor,
        ProbationThreshold: 0.40,
        EarnedThreshold: 0.80,
        RatchetConfirmations: 3,
        Ceilings: new Dictionary<ChangeClass, double>
        {
            [ChangeClass.Unknown] = 0.40,        // capped at probation — never auto
            [ChangeClass.DocsOnly] = 1.00,
            [ChangeClass.DefaultOffFlag] = 1.00,
            [ChangeClass.ApplicationCode] = 1.00,
            [ChangeClass.InfraConfig] = 1.00,
            [ChangeClass.TrustPlane] = 0.40,     // HARD CAP — deterministic-escalate floor
        });

    /// <summary>The per-class score ceiling (defaults to 1.0 for any class not listed).</summary>
    public double CeilingFor(ChangeClass changeClass) =>
        Ceilings.TryGetValue(changeClass, out var c) ? c : 1.0;

    /// <summary>
    /// True when this class is <i>structurally</i> barred from ever earning auto-proceed
    /// because its ceiling sits below the earned bar — the trust-plane invariant, expressed
    /// as data rather than a special-case branch.
    /// </summary>
    public bool IsAutoProceedForbidden(ChangeClass changeClass) => CeilingFor(changeClass) < EarnedThreshold;
}
