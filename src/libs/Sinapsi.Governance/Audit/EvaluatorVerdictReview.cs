namespace Sinapsi.Governance.Audit;

/// <summary>
/// The subject an independent auditor reviews: one delivery-evaluator verdict, described
/// by its EFFECT and outcome — deliberately NOT the evaluator's own rationale prose (a
/// fluent justification is not evidence of correctness; docs/64 §2). The auditor forms its
/// own view from the change facts, then compares.
/// </summary>
public sealed record EvaluatorVerdictReview(
    string CorrelationId,
    ChangeClass ChangeClass,
    string EvaluatorVerdict,
    bool AutoProceeded,
    string ChangeEffectSummary);
