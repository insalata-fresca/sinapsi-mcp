namespace Sinapsi.Governance.Accountability;

/// <summary>
/// The named accountability for delivery-evaluator verdicts — closing the gap the review
/// flagged: Q1 (identity→tool), Q2 (command-safety) and Q3 (operator-gate) each already
/// have an owner, but the delivery evaluator did not. This is where it is named
/// (home-server <c>docs/66</c> "Named accountable owner").
///
/// <para><b>The named accountable owner for delivery-evaluator verdicts is the Operator
/// (Stefano, insalata.fresca)</b> — the Third-Line human. The evaluator (First line) may
/// make routine <c>allow</c> calls autonomously where trust is <see cref="AutoProceedAuthority.Earned"/>,
/// and this governance layer (Second line) monitors it, but ACCOUNTABILITY for what the
/// pipeline decides rests with a named human, never with the agent that made the call.
/// That is the whole point of §2's correction: an agent must not be its own gate.</para>
/// </summary>
public static class DeliveryEvaluatorAccountability
{
    /// <summary>First line — makes the verdict.</summary>
    public static readonly AccountableOwner FirstLine = new(
        Line: LineOfDefense.First,
        Role: "Delivery Evaluator (pipeline verdict maker)",
        Named: "Sinapsi.DeliveryEvaluator (C1) — deterministic risk classifier",
        Mechanism: "static path/content classification against the docs/65 rubric (NOT an LLM)");

    /// <summary>Second line — monitors trust over time.</summary>
    public static readonly AccountableOwner SecondLine = new(
        Line: LineOfDefense.Second,
        Role: "Continuous-trust governance (risk oversight)",
        Named: "Sinapsi.Governance (D1) — trust ledger + escalation SLO + retrospective inspection",
        Mechanism: "graduated trust math + rate SLO + scheduled sampling over shadow outcomes");

    /// <summary>
    /// Third line — the INDEPENDENT audit + the ultimate accountable owner. This is the
    /// named answer: the human operator, reached by a different mechanism than the evaluator.
    /// </summary>
    public static readonly AccountableOwner ThirdLine = new(
        Line: LineOfDefense.Third,
        Role: "Accountable Owner for pipeline verdicts (independent audit + escalation floor)",
        Named: "Operator — Stefano (insalata.fresca)",
        Mechanism: "human review of the independent audit line + retrospective inspection samples");

    /// <summary>The single named accountable owner the review asked us to name.</summary>
    public static AccountableOwner AccountableOwner => ThirdLine;

    /// <summary>All three lines, first→third.</summary>
    public static readonly IReadOnlyList<AccountableOwner> Lines = new[] { FirstLine, SecondLine, ThirdLine };
}
