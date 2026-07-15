using System.Security.Cryptography;
using System.Text;

namespace Sinapsi.DeliveryEvaluator.Grading;

/// <summary>
/// Grades the C1 independent evaluator (<see cref="Sinapsi.DeliveryEvaluator.DeterministicRiskClassifier"/>,
/// driven through its own <see cref="Sinapsi.DeliveryEvaluator.CorpusScenarioAdapter"/>) against the
/// B1 golden corpus — the runnable "evaluate-the-evaluator" pass (home-server <c>docs/64 §3</c>,
/// <c>datasets/risk-rubric/README.md</c>).
///
/// <para>REUSES C1 by construction: the evaluator is fed each scenario's <c>diff_summary</c> ONLY
/// (labels withheld), exactly as the README prescribes. This grader adds no scoring intelligence of
/// its own to the evaluator — it measures it.</para>
/// </summary>
public static class CorpusGrader
{
    /// <summary>Run the evaluator over one scenario, withholding the answer key.</summary>
    public static ScenarioGrade Evaluate(LabelledScenario s)
    {
        var change = CorpusScenarioAdapter.ToChangeSet(s.DiffSummary, correlationId: s.Id);
        var v = DeterministicRiskClassifier.Classify(change);
        return new ScenarioGrade(s, v.Verdict, v.Tier, v.Confidence);
    }

    /// <summary>Grade the whole corpus and build the full scorecard, running the classifier
    /// <paramref name="runs"/> times to prove run-to-run determinism (variance).</summary>
    public static ScoreCard Grade(IReadOnlyList<LabelledScenario> corpus, int runs = 2)
    {
        if (corpus is null || corpus.Count == 0)
            throw new ArgumentException("corpus is empty", nameof(corpus));
        if (runs < 2)
            throw new ArgumentOutOfRangeException(nameof(runs), "variance needs at least two runs");

        var grades = corpus.Select(Evaluate).ToList();

        // --- Variance: repeat the run and record any scenario whose verdict changed. ---
        var disagreements = new List<string>();
        for (int r = 1; r < runs; r++)
            foreach (var s in corpus)
            {
                var again = Evaluate(s).Predicted;
                var first = grades.First(g => g.Scenario.Id == s.Id).Predicted;
                if (again != first) disagreements.Add(s.Id);
            }
        disagreements = disagreements.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();

        // --- Corpus shape ---
        var perTierCount = corpus.GroupBy(s => s.Tier, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var perVerdictCount = corpus.GroupBy(s => s.CorrectVerdict, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var trustPlane = grades.Where(g => g.Scenario.IsTrustPlane).ToList();
        var allowLowTier = grades.Where(g => g.Scenario.IsAllowLabelledLowTier).ToList();
        var allAllowLabelled = grades.Where(g => g.Expected == Verdict.Allow).ToList();

        var shape = new CorpusShape(
            Total: corpus.Count,
            PerTier: Sorted(perTierCount),
            PerVerdict: Sorted(perVerdictCount),
            Adversarial: corpus.Count(s => s.IsAdversarial),
            TrustPlaneMustEscalate: trustPlane.Count,
            AllowLabelledLowTier: allowLowTier.Count);

        // --- Metrics ---
        var adversarial = grades.Where(g => g.Scenario.IsAdversarial).ToList();
        var honest = grades.Where(g => !g.Scenario.IsAdversarial).ToList();

        var escMisses = trustPlane.Where(g => g.IsEscalationMiss)
            .Select(g => g.Scenario.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var overBlocked = allowLowTier.Where(g => g.IsOverBlock)
            .Select(g => g.Scenario.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();

        var metrics = new Metrics(
            OverallAccuracy: Ratio.Of(grades.Count(g => g.IsExactMatch), grades.Count),
            HonestAccuracy: Ratio.Of(honest.Count(g => g.IsExactMatch), honest.Count),
            AdversarialAccuracy: Ratio.Of(adversarial.Count(g => g.IsExactMatch), adversarial.Count),
            EscalationRecall: new EscalationRecall(
                Ratio.Of(trustPlane.Count - escMisses.Count, trustPlane.Count), escMisses),
            FalseRefusal: new FalseRefusal(
                LowTier: Ratio.Of(overBlocked.Count, allowLowTier.Count),
                AllAllowLabelled: Ratio.Of(allAllowLabelled.Count(g => g.Predicted != Verdict.Allow), allAllowLabelled.Count),
                OverBlockedIds: overBlocked),
            Variance: new VarianceResult(runs, disagreements));

        // --- Safety invariants ---
        var falseAllows = grades.Where(g => g.IsFalseAllow)
            .Select(g => $"{g.Scenario.Id} (expected {g.Scenario.CorrectVerdict})")
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        var denyLeaks = grades.Where(g => g.Expected == Verdict.Deny && g.Predicted == Verdict.Allow)
            .Select(g => g.Scenario.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var safety = new Safety(falseAllows, denyLeaks);

        // --- Per-tier breakdown ---
        var perTier = perTierCount.Keys.OrderBy(TierOrder).Select(tier =>
        {
            var rows = grades.Where(g => string.Equals(g.Scenario.Tier, tier, StringComparison.Ordinal)).ToList();
            return new TierBreakdown(
                Tier: tier,
                N: rows.Count,
                Accuracy: Ratio.Of(rows.Count(g => g.IsExactMatch), rows.Count),
                PredictedAllow: rows.Count(g => g.Predicted == Verdict.Allow),
                PredictedRequiresApproval: rows.Count(g => g.Predicted == Verdict.RequiresApproval),
                PredictedDeny: rows.Count(g => g.Predicted == Verdict.Deny));
        }).ToList();

        // --- Readiness ---
        var readiness = ReadinessGate.Derive(metrics, perTier, corpus.Count);

        // --- Per-scenario audit rows ---
        var rowsOut = grades.OrderBy(g => g.Scenario.Id, StringComparer.Ordinal).Select(g => new ScenarioRow(
            Id: g.Scenario.Id,
            Tier: g.Scenario.Tier,
            Adversarial: g.Scenario.IsAdversarial,
            Expected: g.Scenario.CorrectVerdict,
            Predicted: g.Predicted.ToToken(),
            PredictedTier: g.PredictedTier.ToString(),
            Confidence: g.Confidence.ToString(),
            ExactMatch: g.IsExactMatch,
            EscalationMiss: g.IsEscalationMiss,
            OverBlock: g.IsOverBlock,
            FalseAllow: g.IsFalseAllow)).ToList();

        return new ScoreCard(
            Mission: "B2 — evaluate-the-evaluator (delivery risk evaluator)",
            EvaluatorUnderTest: "Sinapsi.DeliveryEvaluator.DeterministicRiskClassifier (C1, merged #112)",
            CorpusFingerprint: Fingerprint(corpus),
            Corpus: shape,
            Metrics: metrics,
            Safety: safety,
            PerTier: perTier,
            Readiness: readiness,
            PerScenario: rowsOut);
    }

    /// <summary>A content fingerprint of the answer key (ids + labels, order-independent) so the
    /// scorecard is bound to the exact corpus it graded.</summary>
    public static string Fingerprint(IReadOnlyList<LabelledScenario> corpus)
    {
        var sb = new StringBuilder();
        foreach (var s in corpus.OrderBy(s => s.Id, StringComparer.Ordinal))
            sb.Append(s.Id).Append('|').Append(s.Tier).Append('|')
              .Append(s.CorrectVerdict).Append('|').Append(s.IsAdversarial ? '1' : '0').Append('\n');
        return "sha256:" + Hashing.Sha256Hex(sb.ToString());
    }

    private static IReadOnlyDictionary<string, int> Sorted(Dictionary<string, int> d) =>
        d.OrderBy(kv => TierOrder(kv.Key)).ThenBy(kv => kv.Key, StringComparer.Ordinal)
         .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    // Stable tier ordering for legible output (matches rubric severity).
    private static int TierOrder(string tier) => tier switch
    {
        "docs-only" => 1,
        "default-off-flag" => 2,
        "application-code" => 3,
        "infra-config" => 4,
        "trust-plane" => 5,
        "allow" => 1, "requiresApproval" => 2, "deny" => 3, // verdict ordering reuse
        _ => 99,
    };
}

// Small helper so we don't pull a full hashing dance inline.
internal static class Hashing
{
    public static string Sha256Hex(string s)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
