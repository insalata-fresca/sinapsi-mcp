using Sinapsi.DeliveryEvaluator.Grading;
using Xunit;
using Xunit.Abstractions;

namespace Sinapsi.DeliveryEvaluator.Grading.Tests;

/// <summary>
/// The B2 scorecard: grade the C1 evaluator over the B1 golden corpus and assert the gate bars,
/// the corpus shape, and the safety invariants. Also keeps the committed scorecard honest (golden
/// file) and regenerates it on demand.
/// </summary>
public class ScorecardTests
{
    private readonly ITestOutputHelper _out;
    public ScorecardTests(ITestOutputHelper output) => _out = output;

    private static ScoreCard Grade() => CorpusGrader.Grade(CorpusLoader.Load(), runs: 2);

    [Fact]
    public void corpus_shape_matches_the_b1_answer_key_readme()
    {
        var c = Grade();
        Assert.Equal(60, c.Corpus.Total);
        Assert.Equal(24, c.Corpus.TrustPlaneMustEscalate);   // escalation-recall denominator
        Assert.Equal(25, c.Corpus.Adversarial);              // adversarial subset
        Assert.Equal(8, c.Corpus.PerTier["docs-only"]);
        Assert.Equal(7, c.Corpus.PerTier["default-off-flag"]);
        Assert.Equal(10, c.Corpus.PerTier["application-code"]);
        Assert.Equal(11, c.Corpus.PerTier["infra-config"]);
        Assert.Equal(24, c.Corpus.PerTier["trust-plane"]);
        Assert.Equal(14, c.Corpus.PerVerdict["allow"]);
        Assert.Equal(35, c.Corpus.PerVerdict["requiresApproval"]);
        Assert.Equal(11, c.Corpus.PerVerdict["deny"]);
    }

    [Fact]
    public void escalation_recall_is_100pct_on_the_trust_plane_must_escalate_stratum()
    {
        // THE gate metric. A single trust-plane auto-allow is the critical miss the track prevents.
        var c = Grade();
        Assert.True(c.Metrics.EscalationRecall.IsTotal,
            "escalation-recall < 100% — trust-plane auto-allow(s): " + string.Join(", ", c.Metrics.EscalationRecall.Misses));
        Assert.Equal(1.0, c.Metrics.EscalationRecall.Recall.Value);
    }

    [Fact]
    public void no_false_allow_and_no_deny_leak()
    {
        var c = Grade();
        Assert.Empty(c.Safety.FalseAllows);
        Assert.Empty(c.Safety.DenyLeaks);
    }

    [Fact]
    public void classifier_is_deterministic_zero_variance()
    {
        var c = Grade();
        Assert.True(c.Metrics.Variance.IsDeterministic,
            "non-deterministic on: " + string.Join(", ", c.Metrics.Variance.Disagreements));
    }

    [Fact]
    public void over_block_on_low_tiers_is_within_the_agreed_bar()
    {
        var c = Grade();
        Assert.True(c.Metrics.FalseRefusal.LowTier.Value <= c.Readiness.Bar.FalseRefusalMax,
            $"over-block {c.Metrics.FalseRefusal.LowTier.Pct}% exceeds the {c.Readiness.Bar.FalseRefusalMax:P0} bar; " +
            "ids: " + string.Join(", ", c.Metrics.FalseRefusal.OverBlockedIds));
    }

    [Fact]
    public void gate_metrics_pass_but_overall_verdict_is_not_ready_for_enforcement_on_the_seed()
    {
        // The honest floor: a green seed is NECESSARY, not SUFFICIENT. Enforcement stays NOT-READY.
        var c = Grade();
        Assert.True(c.Readiness.GateMetricsPass);
        Assert.Equal("NOT-READY-FOR-ENFORCEMENT", c.Readiness.OverallVerdict);
        Assert.All(c.Readiness.ByEnforcementLayer, v => Assert.False(v.Ready));
        Assert.All(c.Readiness.ByTier, v => Assert.False(v.Ready));
    }

    [Fact]
    public void committed_scorecard_matches_a_fresh_grade_or_is_regenerated()
    {
        var fresh = Grade().ToJson();

        // Regen mode: write the committed artifacts (json + human md) and pass.
        if (Environment.GetEnvironmentVariable("B2_REGEN") == "1")
        {
            var dir = ScorecardArtifacts.Dir();
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "scorecard.json"), fresh);
            File.WriteAllText(Path.Combine(dir, "SCORECARD.md"), ScoreCardMarkdown.Render(Grade()));
            _out.WriteLine("regenerated scorecard artifacts in " + dir);
            return;
        }

        // Compare mode: the golden must be present next to the test binary and byte-match a fresh grade.
        var golden = Path.Combine(AppContext.BaseDirectory, "fixtures", "scorecard.golden.json");
        Assert.True(File.Exists(golden),
            "committed scorecard.json is missing — run the grading test with B2_REGEN=1 to generate it");
        var committed = File.ReadAllText(golden);
        Assert.True(Normalize(committed) == Normalize(fresh),
            "committed scorecard.json drifted from a fresh grade — regenerate with B2_REGEN=1 and review the diff");
    }

    // Compare on content, tolerant of trailing-newline / CRLF differences introduced by git/editors.
    private static string Normalize(string s) => s.Replace("\r\n", "\n").TrimEnd('\n');
}
