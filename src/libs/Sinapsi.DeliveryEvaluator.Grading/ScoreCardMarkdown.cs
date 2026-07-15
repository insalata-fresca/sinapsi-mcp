using System.Text;

namespace Sinapsi.DeliveryEvaluator.Grading;

/// <summary>Renders a <see cref="ScoreCard"/> as the committed human-readable summary
/// (the operator-facing half of the B2 deliverable). Deterministic.</summary>
public static class ScoreCardMarkdown
{
    /// <summary>Render the full human summary + readiness verdict.</summary>
    public static string Render(ScoreCard c)
    {
        var m = c.Metrics;
        var sb = new StringBuilder();

        sb.AppendLine("# B2 scorecard — evaluate-the-evaluator (delivery risk evaluator)");
        sb.AppendLine();
        sb.AppendLine("> **Generated, do not hand-edit.** Produced by `Sinapsi.DeliveryEvaluator.Grading`");
        sb.AppendLine("> (`CorpusGrader.Grade`) over the vendored B1 golden corpus, by reusing the C1 evaluator");
        sb.AppendLine("> through its own `CorpusScenarioAdapter`. Regenerate with the grading test in `B2_REGEN=1`");
        sb.AppendLine("> mode; a drift between this file and a fresh run fails the golden-file CI test.");
        sb.AppendLine();
        sb.AppendLine($"- **Mission:** {c.Mission}");
        sb.AppendLine($"- **Evaluator under test:** {c.EvaluatorUnderTest}");
        sb.AppendLine($"- **Corpus fingerprint:** `{c.CorpusFingerprint}`");
        sb.AppendLine($"- **Corpus:** {c.Corpus.Total} scenarios · trust-plane MUST-escalate={c.Corpus.TrustPlaneMustEscalate} · " +
                      $"adversarial={c.Corpus.Adversarial} · allow-labelled low-tier={c.Corpus.AllowLabelledLowTier}");
        sb.AppendLine();

        sb.AppendLine("## Metric table");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value | Detail |");
        sb.AppendLine("|---|---|---|");
        sb.AppendLine($"| Overall accuracy | **{m.OverallAccuracy.Pct}%** | {m.OverallAccuracy.Numerator}/{m.OverallAccuracy.Denominator} |");
        sb.AppendLine($"| Honest-case accuracy | {m.HonestAccuracy.Pct}% | {m.HonestAccuracy.Numerator}/{m.HonestAccuracy.Denominator} (non-adversarial) |");
        sb.AppendLine($"| Adversarial-subset accuracy | {m.AdversarialAccuracy.Pct}% | {m.AdversarialAccuracy.Numerator}/{m.AdversarialAccuracy.Denominator} (injection surface) |");
        sb.AppendLine($"| **Escalation-recall (THE gate)** | **{m.EscalationRecall.Recall.Pct}%** | {m.EscalationRecall.Recall.Numerator}/{m.EscalationRecall.Recall.Denominator} trust-plane MUST-escalate; misses: {Misses(m.EscalationRecall.Misses)} |");
        sb.AppendLine($"| False-refusal / over-block (low tiers) | {m.FalseRefusal.LowTier.Pct}% | {m.FalseRefusal.LowTier.Numerator}/{m.FalseRefusal.LowTier.Denominator}; ids: {Misses(m.FalseRefusal.OverBlockedIds)} |");
        sb.AppendLine($"| False-refusal (all allow-labelled) | {m.FalseRefusal.AllAllowLabelled.Pct}% | {m.FalseRefusal.AllAllowLabelled.Numerator}/{m.FalseRefusal.AllAllowLabelled.Denominator} (informational) |");
        sb.AppendLine($"| Variance (run-twice determinism) | {(m.Variance.IsDeterministic ? "0 (deterministic)" : $"{m.Variance.Disagreements.Count} disagree")} | {m.Variance.Runs} runs; {Misses(m.Variance.Disagreements)} |");
        sb.AppendLine();

        sb.AppendLine("## Safety invariants");
        sb.AppendLine();
        sb.AppendLine($"- **False-allows** (allowed something the rubric escalates/denies): {(c.Safety.FalseAllows.Count == 0 ? "**none**" : string.Join(", ", c.Safety.FalseAllows))}");
        sb.AppendLine($"- **Deny-leaks** (a `deny` case allowed): {(c.Safety.DenyLeaks.Count == 0 ? "**none**" : string.Join(", ", c.Safety.DenyLeaks))}");
        sb.AppendLine();

        sb.AppendLine("## Per-tier breakdown");
        sb.AppendLine();
        sb.AppendLine("| Tier | n | Accuracy | predicted allow / requiresApproval / deny |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var t in c.PerTier)
            sb.AppendLine($"| {t.Tier} | {t.N} | {t.Accuracy.Pct}% | {t.PredictedAllow} / {t.PredictedRequiresApproval} / {t.PredictedDeny} |");
        sb.AppendLine();

        sb.AppendLine("## Enforcement-readiness verdict");
        sb.AppendLine();
        sb.AppendLine($"**Bar:** escalation-recall ≥ {Pct(c.Readiness.Bar.EscalationRecallMin)} on the MUST-escalate stratum " +
                      $"· over-block ≤ {Pct(c.Readiness.Bar.FalseRefusalMax)} on the low tiers · variance = 0.");
        sb.AppendLine($"**Gate metrics pass on the seed:** {(c.Readiness.GateMetricsPass ? "YES (necessary, not sufficient)" : "NO")}.");
        sb.AppendLine();
        sb.AppendLine($"### Overall: {c.Readiness.OverallVerdict}");
        sb.AppendLine();
        sb.AppendLine(c.Readiness.OverallRationale);
        sb.AppendLine();
        sb.AppendLine("### Per rubric tier");
        sb.AppendLine();
        foreach (var v in c.Readiness.ByTier)
            sb.AppendLine($"- **{v.Layer} — {v.Word}.** {v.Rationale}");
        sb.AppendLine();
        sb.AppendLine("### Per enforcement layer (docs/64 §4 sequencing)");
        sb.AppendLine();
        foreach (var v in c.Readiness.ByEnforcementLayer)
            sb.AppendLine($"- **{v.Layer} — {v.Word}.** {v.Rationale}");
        sb.AppendLine();

        sb.AppendLine("## Per-scenario audit table");
        sb.AppendLine();
        sb.AppendLine("| id | tier | adv | expected | predicted | pred-tier | conf | match | esc-miss | over-block |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in c.PerScenario)
            sb.AppendLine($"| {r.Id} | {r.Tier} | {(r.Adversarial ? "Y" : "")} | {r.Expected} | {r.Predicted} | {r.PredictedTier} | {r.Confidence} | {(r.ExactMatch ? "yes" : "NO")} | {(r.EscalationMiss ? "MISS" : "")} | {(r.OverBlock ? "OVER" : "")} |");
        sb.AppendLine();

        sb.AppendLine("## Live-wire follow-on");
        sb.AppendLine();
        sb.AppendLine("The shadow-vs-would-enforce **stream-diff framework** (`StreamDiffHarness` + `IShadowDecisionSource`)");
        sb.AppendLine("ships here and is CI-tested over a JSONL fixture. Wiring it to the **live** shadow bus is deferred");
        sb.AppendLine("because (a) the sinapsi-mcp CI container cannot reach NATS, and (b) the as-built verdict-fact envelope");
        sb.AppendLine("does not carry the raw change needed to recompute would-enforce. Follow-on: a `NatsShadowDecisionSource`");
        sb.AppendLine("consuming `homelab.security.authz.delivery-evaluator.>` joined to the change by `correlation_id`.");
        return sb.ToString();
    }

    private static string Misses(IReadOnlyList<string> ids) => ids.Count == 0 ? "none" : string.Join(", ", ids);
    private static string Pct(double v) => $"{Math.Round(v * 100, 2)}%";
}
