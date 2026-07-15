namespace Sinapsi.Nats.EventPlane;

/// <summary>How severe the current error-budget burn is, from a multi-window reading.</summary>
public enum BurnSeverity
{
    /// <summary>Burn within budget — no signal.</summary>
    Nominal,
    /// <summary>Burn above the elevated threshold on at least one window — a signal worth weighing.</summary>
    Elevated,
    /// <summary>Fast-burn above the critical threshold — the strongest signal.</summary>
    Critical,
}

/// <summary>How the burn-rate signal is allowed to influence the act-path. Crucially there is NO
/// "BlockingGate" value: the canon forbids burn-rate being a blunt auto-allow/deny (home-server
/// <c>docs/64 §3</c>, reconciling Observability error-budget gating vs Platform-Eng "error budgets
/// are optional"). It is only ever an input to judgment and/or an escalation trigger.</summary>
public enum BudgetMode
{
    /// <summary>Error budgets are optional here (Platform-Eng stance): the signal is surfaced as an
    /// input to judgment but never on its own triggers an escalation. Never blocks.</summary>
    Advisory,
    /// <summary>Error budgets are watched here (Observability stance): a non-nominal signal TRIGGERS
    /// an escalation to the human floor — but still never auto-denies the action itself.</summary>
    EscalationTrigger,
}

/// <summary>A multi-window burn-rate reading, expressed as multiples of the sustainable
/// budget-consumption baseline (1.0 = exactly on budget). A short "fast" window catches acute
/// regressions; a long "slow" window catches sustained drains.</summary>
/// <param name="FastBurn">Burn multiple over the short window (e.g. 1h).</param>
/// <param name="SlowBurn">Burn multiple over the long window (e.g. 6h).</param>
public sealed record BurnRateReading(double FastBurn, double SlowBurn);

/// <summary>Thresholds for classifying a <see cref="BurnRateReading"/>.</summary>
/// <param name="ElevatedAt">Burn multiple (on any window) at/above which the signal is Elevated.</param>
/// <param name="CriticalAt">Fast-burn multiple at/above which the signal is Critical.</param>
public sealed record BurnRateThresholds(double ElevatedAt, double CriticalAt)
{
    /// <summary>A conventional multi-window default (elevated at 2×, critical fast-burn at 14.4× —
    /// the classic 1h fast-burn figure). Tune per SLO; these are only defaults.</summary>
    public static BurnRateThresholds Default { get; } = new(ElevatedAt: 2.0, CriticalAt: 14.4);
}

/// <summary>
/// The judgment INPUT derived from a burn-rate reading — never a verdict. <see cref="IsAllowDenyGate"/>
/// is hard-wired false: this type structurally cannot express "deny because the budget burned". It
/// carries the severity (an input to a human/independent-evaluator judgment) and, in
/// <see cref="BudgetMode.EscalationTrigger"/>, whether the human floor should be paged.
/// </summary>
/// <param name="Severity">The classified burn severity.</param>
/// <param name="TriggersEscalation">Whether this reading escalates to the human floor.</param>
/// <param name="Rationale">Human-readable explanation (audit).</param>
public sealed record BurnRateAssessment(BurnSeverity Severity, bool TriggersEscalation, string Rationale)
{
    /// <summary>ALWAYS false, by construction. Burn-rate is an escalation trigger and an input to
    /// judgment — never a blunt auto-allow/deny gate (home-server <c>docs/64 §3</c>). Present as an
    /// explicit, testable invariant so no future caller can repurpose this into a gate.</summary>
    public bool IsAllowDenyGate => false;
}

/// <summary>
/// Classifies a multi-window <see cref="BurnRateReading"/> into a <see cref="BurnRateAssessment"/> —
/// a judgment input / escalation trigger, NOT a gate. This is the reconciliation the canon asks for:
/// under <see cref="BudgetMode.Advisory"/> the budget is optional and the signal never escalates on
/// its own; under <see cref="BudgetMode.EscalationTrigger"/> a non-nominal signal pages the floor.
/// Neither mode ever denies — that decision belongs to the (independent) evaluator/human, informed by
/// this signal among others.
/// </summary>
public static class BurnRatePolicy
{
    /// <summary>Assess a reading. Severity comes from the worse of the two windows (critical is
    /// fast-burn-only, as a slow window cannot move fast enough to be "acute").</summary>
    public static BurnRateAssessment Assess(BurnRateReading reading, BurnRateThresholds thresholds, BudgetMode mode)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(thresholds);

        var severity = Classify(reading, thresholds);
        var escalates = mode == BudgetMode.EscalationTrigger && severity != BurnSeverity.Nominal;

        var rationale = severity switch
        {
            BurnSeverity.Critical => $"fast-burn {reading.FastBurn:0.##}× ≥ critical {thresholds.CriticalAt:0.##}× — strong signal",
            BurnSeverity.Elevated => $"burn elevated (fast {reading.FastBurn:0.##}×, slow {reading.SlowBurn:0.##}×, threshold {thresholds.ElevatedAt:0.##}×)",
            _ => $"burn nominal (fast {reading.FastBurn:0.##}×, slow {reading.SlowBurn:0.##}×)",
        };
        var modeNote = mode == BudgetMode.Advisory
            ? " — advisory: input to judgment only, no escalation, never a gate"
            : (escalates ? " — escalation triggered to the human floor (still not a gate)" : " — no escalation");

        return new BurnRateAssessment(severity, escalates, rationale + modeNote);
    }

    private static BurnSeverity Classify(BurnRateReading reading, BurnRateThresholds thresholds)
    {
        if (reading.FastBurn >= thresholds.CriticalAt) return BurnSeverity.Critical;
        if (reading.FastBurn >= thresholds.ElevatedAt || reading.SlowBurn >= thresholds.ElevatedAt) return BurnSeverity.Elevated;
        return BurnSeverity.Nominal;
    }
}
