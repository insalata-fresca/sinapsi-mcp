namespace Sinapsi.DeliveryEvaluator;

/// <summary>
/// The evaluator's confidence in a verdict — a required signal (home-server <c>docs/64 §3</c>:
/// "drive proceed/escalate on a measured confidence signal"; <c>docs/65</c> principle 6, README
/// metric 5: B2 measures confidence↔outcome correlation).
///
/// <para>For a DETERMINISTIC classifier confidence reflects <b>signal strength</b>, not a model
/// logprob: an explicit trust-plane path/value match or a hard-floor signal is
/// <see cref="High"/>; a positive low-tier clearance that leans on heuristic content signals is
/// <see cref="Medium"/>; a fail-safe escalation reached with no positive signal (uncertainty /
/// unknown surface) is <see cref="Low"/>. Per <c>docs/65</c> principle 6 a HIGH confidence on a
/// trust-plane change is a reason for suspicion, not trust — B2 grades that, the evaluator only
/// reports it.</para>
/// </summary>
public enum Confidence
{
    /// <summary>Fail-safe escalation with no positive clearing/blocking signal — the honest
    /// "I could not classify this, so I escalate" case.</summary>
    Low,

    /// <summary>A positive low-tier clearance (allow) or promotion that rests on heuristic
    /// content signals rather than a definitive surface match.</summary>
    Medium,

    /// <summary>An explicit, definitive match: a trust-plane surface, a hard-floor deny signal,
    /// or a path-proven docs-only clearance.</summary>
    High,
}
