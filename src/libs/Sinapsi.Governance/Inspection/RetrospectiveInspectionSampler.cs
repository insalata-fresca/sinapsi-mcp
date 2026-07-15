namespace Sinapsi.Governance.Inspection;

/// <summary>
/// Draws inspection samples of auto-proceed decisions for human review — the "inspected
/// trust" control (docs/64 §3). Sampling is <b>seeded and deterministic</b> (a caller-supplied
/// seed drives the RNG), so a given population + seed always yields the same draw: reproducible
/// for tests and for audit ("show me exactly what was reviewed on day X").
///
/// <para>Only auto-proceed decisions are eligible — a decision the operator already saw
/// (an escalation) needs no retrospective inspection.</para>
/// </summary>
public sealed class RetrospectiveInspectionSampler
{
    private readonly InspectionSamplerConfig _config;
    private readonly Func<DateTimeOffset> _clock;

    public RetrospectiveInspectionSampler(InspectionSamplerConfig? config = null, Func<DateTimeOffset>? clock = null)
    {
        _config = config ?? InspectionSamplerConfig.Default;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// A periodic retrospective draw: <see cref="InspectionSamplerConfig.PeriodicSampleRate"/>
    /// of the auto-proceed decisions in <paramref name="decisions"/>, chosen uniformly at
    /// random under <paramref name="seed"/> (at least one when the population is non-empty and
    /// the rate is &gt; 0, so a quiet period is still spot-checked).
    /// </summary>
    public InspectionSample SamplePeriodic(IEnumerable<AutoProceedDecision> decisions, int seed)
    {
        var pool = AutoProceedOnly(decisions);
        int take = pool.Count == 0 ? 0 : Math.Max(1, (int)Math.Ceiling(pool.Count * _config.PeriodicSampleRate));
        return Draw(pool, take, seed, InspectionReason.PeriodicRetrospective);
    }

    /// <summary>
    /// The daily North-Star draw: up to <see cref="InspectionSamplerConfig.DailyNorthStarSize"/>
    /// auto-proceed decisions from the given day, for the human drift/sycophancy check.
    /// </summary>
    public InspectionSample DailyNorthStar(IEnumerable<AutoProceedDecision> decisions, DateOnly day, int seed)
    {
        var pool = AutoProceedOnly(decisions)
            .Where(d => DateOnly.FromDateTime(d.DecidedAt.UtcDateTime) == day)
            .ToList();
        return Draw(pool, Math.Min(_config.DailyNorthStarSize, pool.Count), seed, InspectionReason.DailyNorthStar);
    }

    private InspectionSample Draw(List<AutoProceedDecision> pool, int take, int seed, InspectionReason reason)
    {
        // Deterministic partial Fisher–Yates: shuffle the first `take` slots under the seed,
        // then keep them. Stable ordering of the input pool (by DecidedAt, then correlation)
        // makes the draw reproducible regardless of enumeration order.
        var ordered = pool
            .OrderBy(d => d.DecidedAt)
            .ThenBy(d => d.CorrelationId, StringComparer.Ordinal)
            .ToList();

        var rng = new Random(seed);
        for (int i = 0; i < take; i++)
        {
            int j = i + rng.Next(ordered.Count - i);
            (ordered[i], ordered[j]) = (ordered[j], ordered[i]);
        }

        var drawn = ordered.Take(take).ToList();
        return new InspectionSample(reason, drawn, pool.Count, _clock());
    }

    private static List<AutoProceedDecision> AutoProceedOnly(IEnumerable<AutoProceedDecision> decisions) =>
        decisions.Where(d => d.AutoProceeded).ToList();
}
