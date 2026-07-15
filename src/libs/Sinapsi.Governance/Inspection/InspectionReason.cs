namespace Sinapsi.Governance.Inspection;

/// <summary>Why a set of decisions was pulled for human inspection.</summary>
public enum InspectionReason
{
    /// <summary>Scheduled periodic sample of auto-proceed decisions — inspection is
    /// time-triggered, NOT only fired by an incident (docs/64 §3 "scheduled retrospective
    /// inspection"). Catches quiet drift that never trips an alert.</summary>
    PeriodicRetrospective = 0,

    /// <summary>The daily human North-Star sample: a small fixed-size draw reviewed every day
    /// to catch drift / sycophancy the automated checks miss (docs/64 §3).</summary>
    DailyNorthStar = 1,
}
