namespace Sinapsi.Governance.Inspection;

/// <summary>An immutable draw of decisions queued for human review, with why it was drawn.</summary>
public sealed record InspectionSample(
    InspectionReason Reason,
    IReadOnlyList<AutoProceedDecision> Decisions,
    int PopulationSize,
    DateTimeOffset DrawnAt);
