namespace Sinapsi.Governance.Aia;

/// <summary>
/// An Algorithmic-Impact-Assessment stub per change tier — the "should this decision class
/// be automated at all, and under what oversight" record that a governed autonomous gate is
/// expected to carry (docs/64 §3 "AIA per change tier"). This is a STUB: it frames the
/// questions and pins the tier-appropriate answers; a full AIA is an operator artifact.
/// </summary>
public sealed record AlgorithmicImpactAssessment(
    ChangeClass ChangeClass,
    string DecisionScope,
    string PotentialHarms,
    string HumanOversight,
    string Reversibility,
    string FallbackOnUncertainty,
    bool AutomationPermitted);
