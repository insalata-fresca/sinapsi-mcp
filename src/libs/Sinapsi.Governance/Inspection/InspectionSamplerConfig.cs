namespace Sinapsi.Governance.Inspection;

/// <summary>
/// Sampling policy for retrospective inspection. <see cref="PeriodicSampleRate"/> is the
/// fraction of auto-proceed decisions drawn each period; <see cref="DailyNorthStarSize"/>
/// is the fixed number pulled for the daily human North-Star review.
/// </summary>
public sealed record InspectionSamplerConfig(double PeriodicSampleRate, int DailyNorthStarSize)
{
    /// <summary>5% periodic retrospective sample; 10 decisions/day for the North-Star review.</summary>
    public static InspectionSamplerConfig Default { get; } = new(PeriodicSampleRate: 0.05, DailyNorthStarSize: 10);
}
