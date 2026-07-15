using Xunit;
using Xunit.Abstractions;

namespace Sinapsi.DeliveryEvaluator.Tests;

/// <summary>
/// Runs the C1 evaluator over the vendored B1 seed corpus — the runnable path Mission B2 scores
/// (home-server <c>datasets/risk-rubric/README.md</c>). Per the C1 charter, accuracy GRADING is B2's
/// job; these tests assert only that the evaluator (a) PRODUCES a verdict for every scenario and
/// (b) upholds the load-bearing SAFETY properties — it never auto-allows a trust-plane / deny case,
/// and it never allows anything the answer key says to escalate/deny (no false-allow).
/// </summary>
public class SeedCorpusRunTests
{
    private readonly ITestOutputHelper _out;
    public SeedCorpusRunTests(ITestOutputHelper output) => _out = output;

    private static Verdict Parse(string token) => token switch
    {
        "allow" => Verdict.Allow,
        "requiresApproval" => Verdict.RequiresApproval,
        "deny" => Verdict.Deny,
        _ => throw new InvalidOperationException($"bad label '{token}'"),
    };

    private static (SeedCorpusScenario Scenario, RiskVerdict Verdict) Evaluate(SeedCorpusScenario s)
    {
        // Only diff_summary is fed in — the label fields are withheld (exactly as B2 does).
        var change = CorpusScenarioAdapter.ToChangeSet(s.DiffSummary, correlationId: s.Id);
        return (s, DeterministicRiskClassifier.Classify(change));
    }

    [Fact]
    public void corpus_loads_and_the_evaluator_produces_a_verdict_for_every_scenario()
    {
        var scenarios = SeedCorpus.Load();
        Assert.Equal(60, scenarios.Count);

        foreach (var s in scenarios)
        {
            var (_, v) = Evaluate(s);
            Assert.Contains(v.Verdict, new[] { Verdict.Allow, Verdict.RequiresApproval, Verdict.Deny });
            // Every verdict maps to a shared decision-envelope token.
            Assert.Contains(v.Verdict.ToToken(), Sinapsi.Nats.EventPlane.DecisionEnvelopeContract.Verdicts);
        }
    }

    [Fact]
    public void no_trust_plane_case_is_ever_auto_allowed_escalation_recall_is_total()
    {
        var trustPlane = SeedCorpus.Load().Where(s => s.Tier == "trust-plane").ToList();
        Assert.True(trustPlane.Count >= 24, $"expected the trust-plane stratum, found {trustPlane.Count}");

        var misses = trustPlane.Select(Evaluate).Where(r => r.Verdict.Verdict == Verdict.Allow).ToList();
        Assert.Empty(misses); // a single trust-plane auto-allow is the critical failure the track exists to prevent
    }

    [Fact]
    public void the_evaluator_never_allows_a_change_the_rubric_says_to_escalate_or_deny()
    {
        // Safety property: the evaluator's ALLOW set ⊆ the answer key's ALLOW set (no false-allow).
        var falseAllows = SeedCorpus.Load()
            .Select(Evaluate)
            .Where(r => r.Verdict.Verdict == Verdict.Allow && Parse(r.Scenario.CorrectVerdict) != Verdict.Allow)
            .Select(r => $"{r.Scenario.Id} ({r.Scenario.CorrectVerdict})")
            .ToList();

        Assert.True(falseAllows.Count == 0, "false-allow on: " + string.Join(", ", falseAllows));
    }

    [Fact]
    public void no_deny_labelled_case_is_ever_allowed()
    {
        var leaks = SeedCorpus.Load()
            .Where(s => s.CorrectVerdict == "deny")
            .Select(Evaluate)
            .Where(r => r.Verdict.Verdict == Verdict.Allow)
            .Select(r => r.Scenario.Id)
            .ToList();
        Assert.Empty(leaks);
    }

    [Fact]
    public void directional_report_for_b2()
    {
        // NOT a graded assertion — a legible directional snapshot for the C1 return / B2 hand-off.
        var results = SeedCorpus.Load().Select(Evaluate).ToList();
        int agree = results.Count(r => r.Verdict.Verdict == Parse(r.Scenario.CorrectVerdict));
        int overBlock = results.Count(r =>
            Parse(r.Scenario.CorrectVerdict) == Verdict.Allow && r.Verdict.Verdict != Verdict.Allow);
        int allowLabelled = results.Count(r => r.Scenario.CorrectVerdict == "allow");

        _out.WriteLine($"scenarios          : {results.Count}");
        _out.WriteLine($"exact-match (dir.) : {agree}/{results.Count}");
        _out.WriteLine($"over-block (low)   : {overBlock}/{allowLabelled} allow-labelled");
        _out.WriteLine($"trust-plane allows : 0 (asserted elsewhere)");
        _out.WriteLine("--- per-scenario ---");
        foreach (var r in results)
            _out.WriteLine($"{r.Scenario.Id,-10} label={r.Scenario.CorrectVerdict,-16} verdict={r.Verdict.Verdict,-16} tier={r.Verdict.Tier,-16} conf={r.Verdict.Confidence}");

        Assert.Equal(results.Count, agree + results.Count(r => r.Verdict.Verdict != Parse(r.Scenario.CorrectVerdict)));
    }
}
