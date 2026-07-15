namespace Sinapsi.Governance;

/// <summary>
/// The graded outcome of one shadow delivery decision, fed back into the trust ledger.
/// Reliability is measured in <b>shadow</b> — the evaluator runs alongside the human/
/// deterministic ground truth without enforcing — and only proven reliability ratchets
/// authority up (docs/64 §3, "earn trust before it enforces").
/// </summary>
public enum ShadowOutcome
{
    /// <summary>The shadow verdict matched ground truth (it would have made the right call).
    /// Ratchets the class's score up toward its ceiling.</summary>
    Reliable = 0,

    /// <summary>The shadow verdict was wrong — most gravely, it would have auto-allowed a
    /// change that ground truth escalated/blocked. ANY miss decays the score toward the
    /// baseline and resets the consecutive-reliability streak.</summary>
    Miss = 1,
}
