using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Pipeline.Stages;
using Cervello.Enrichment.Policy;
using Cervello.Enrichment.Ports;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// The grounded text-correction stage (spec <c>text-correction</c>; DESIGN §5.2 step 3). Proves
/// every diff is a reviewable, EVIDENCE-GATED edit vs the base — a glossary term, a resolved
/// participant, or a confident re-ASR — and that the stage NEVER emits an unbacked correction or
/// invents text (the headline negative test). Auto-apply cases run in <c>GradedAutoApply</c>;
/// the escalate-only phase gate is proven separately. Synthetic fixtures only — no personal audio.
/// </summary>
public sealed class CorrectionStageTests
{
    private const string Rec = "rec-2026-07-01-standup";

    private static CorrectionStage Stage(
        IReadOnlyList<CorrectionCandidate> proposals,
        IEnumerable<GlossaryEntry>? glossary = null,
        IReadOnlyDictionary<(int, int), ReAsrResult>? reAsr = null,
        PolicyPhase phase = PolicyPhase.GradedAutoApply,
        FakeReAsrClient? reAsrClient = null) =>
        // These scenarios exercise the re-ASR-ENABLED behaviour (evidence from garbled-span re-ASR).
        // The default-OFF graceful-degrade behaviour is covered by the dedicated tests below.
        new(new FakeCorrectionLlm(proposals),
            new InMemoryCorrectionMapStore(glossary),
            reAsrClient ?? new FakeReAsrClient(reAsr),
            new CorrectionGrader(phase),
            reAsrEnabled: true);

    private static readonly IReadOnlyList<ResolvedParticipant> NoParticipants = Array.Empty<ResolvedParticipant>();
    private static readonly IReadOnlyList<TextSpan> NoGarbled = Array.Empty<TextSpan>();

    // ── Scenario: A glossary term correction is a diff ──────────────────────────────────────────
    [Fact]
    public async Task Glossary_term_correction_is_a_diff_not_a_rewrite()
    {
        const string baseText = "we reviewed the Total Energies contract today";
        var span = SpanOf(baseText, "Total Energies");
        var proposals = new[]
        {
            new CorrectionCandidate(span, "Total Energies", ["TotalEnergies"], CorrectionKind.Term, 0.9),
        };
        var glossary = new[] { new GlossaryEntry("Total Energies", "TotalEnergies", CorrectionKind.Term) };

        var result = await Stage(proposals, glossary).CorrectAsync(Rec, baseText, NoParticipants, NoGarbled);

        var diff = Assert.Single(result.Diffs);
        Assert.Equal(CorrectionKind.Term, diff.Kind);
        Assert.Equal("Total Energies", diff.Before);
        Assert.Equal("TotalEnergies", diff.After);
        Assert.Equal("glossary://Total Energies", diff.EvidenceRef);
        // The base text is UNCHANGED — the correction is a diff, not a rewritten transcript.
        Assert.Equal(baseText, result.BaseText);
    }

    // ── Scenario: A name matching a resolved participant auto-corrects ──────────────────────────
    [Fact]
    public async Task Name_matching_resolved_participant_auto_corrects_carrying_participant_evidence()
    {
        const string baseText = "Gilan said he'd send the numbers";
        var span = SpanOf(baseText, "Gilan");
        var proposals = new[]
        {
            new CorrectionCandidate(span, "Gilan", ["Guilhem"], CorrectionKind.Name, 0.85),
        };
        var participants = new[]
        {
            new ResolvedParticipant("guilhem", "Guilhem", ["Gilan", "Gilhem"]),
        };

        var result = await Stage(proposals).CorrectAsync(Rec, baseText, participants, NoGarbled);

        var diff = Assert.Single(result.Diffs);
        Assert.Equal(CorrectionKind.Name, diff.Kind);
        Assert.Equal("Guilhem", diff.After);
        Assert.Equal("[[guilhem]]", diff.EvidenceRef); // the participant is the evidence
    }

    // ── Scenario: An unbacked term is never invented (HEADLINE) ─────────────────────────────────
    [Fact]
    public async Task Unbacked_correction_is_never_invented_span_left_as_is()
    {
        const string baseText = "the xzzybat metric was flat";
        var span = SpanOf(baseText, "xzzybat");
        // The LLM proposes a "fix" with NO glossary entry, NO participant, and re-ASR is unclear.
        var proposals = new[]
        {
            new CorrectionCandidate(span, "xzzybat", ["XyzBot"], CorrectionKind.Garbled, 0.95),
        };
        var reAsr = new Dictionary<(int, int), ReAsrResult> { [(span.Start, span.End)] = ReAsrResult.Unclear };

        var result = await Stage(proposals, reAsr: reAsr, phase: PolicyPhase.GradedAutoApply)
            .CorrectAsync(Rec, baseText, NoParticipants, [span]);

        // Nothing applied; nothing escalated on a phantom; the gap is recorded, the base untouched.
        Assert.Empty(result.Diffs);
        Assert.Empty(result.OpenPoints);
        var gap = Assert.Single(result.Omitted);
        Assert.Equal(CorrectionOutcome.Omitted, gap.Outcome);
        Assert.Equal(baseText, result.BaseText);
    }

    // ── Scenario: Only low-confidence spans are re-ASR'd ────────────────────────────────────────
    [Fact]
    public async Task ReAsr_runs_only_on_garbled_spans_never_the_whole_transcript()
    {
        const string baseText = "the [inaudible] figure and the Total Energies deal";
        var garbledSpan = SpanOf(baseText, "[inaudible]");
        var termSpan = SpanOf(baseText, "Total Energies");
        var reAsrClient = new FakeReAsrClient(new Dictionary<(int, int), ReAsrResult>
        {
            [(garbledSpan.Start, garbledSpan.End)] = ReAsrResult.Clear("the Q3", 0.88),
        });
        var proposals = new[]
        {
            new CorrectionCandidate(garbledSpan, "[inaudible]", ["the Q3"], CorrectionKind.Garbled, 0.7),
            new CorrectionCandidate(termSpan, "Total Energies", ["TotalEnergies"], CorrectionKind.Term, 0.9),
        };
        var glossary = new[] { new GlossaryEntry("Total Energies", "TotalEnergies", CorrectionKind.Term) };

        var result = await Stage(proposals, glossary, reAsrClient: reAsrClient)
            .CorrectAsync(Rec, baseText, NoParticipants, [garbledSpan]);

        // Re-ASR fired for EXACTLY the one garbled span, not the term span, not the whole transcript.
        Assert.Equal(1, reAsrClient.Calls);
        Assert.Equal(garbledSpan.Start, Assert.Single(reAsrClient.Seen).Start);
        Assert.Equal(2, result.Diffs.Count); // garbled (re-ASR) + term (glossary) both backed
    }

    // ── Re-ASR DISABLED (default): a garbled span is left as-is (omitted); CT126 is never called ──
    [Fact]
    public async Task ReAsr_disabled_leaves_garbled_spans_as_is_without_calling_ct126()
    {
        const string baseText = "the [inaudible] figure and the Total Energies deal";
        var garbledSpan = SpanOf(baseText, "[inaudible]");
        var termSpan = SpanOf(baseText, "Total Energies");
        var reAsrClient = new FakeReAsrClient(new Dictionary<(int, int), ReAsrResult>
        {
            [(garbledSpan.Start, garbledSpan.End)] = ReAsrResult.Clear("the Q3", 0.88),
        });
        var proposals = new[]
        {
            new CorrectionCandidate(garbledSpan, "[inaudible]", ["the Q3"], CorrectionKind.Garbled, 0.7),
            new CorrectionCandidate(termSpan, "Total Energies", ["TotalEnergies"], CorrectionKind.Term, 0.9),
        };
        var glossary = new[] { new GlossaryEntry("Total Energies", "TotalEnergies", CorrectionKind.Term) };

        // Default posture: re-ASR OFF (reAsrEnabled: false is the constructor default).
        var stage = new CorrectionStage(
            new FakeCorrectionLlm(proposals), new InMemoryCorrectionMapStore(glossary),
            reAsrClient, new CorrectionGrader(PolicyPhase.GradedAutoApply));

        var result = await stage.CorrectAsync(Rec, baseText, NoParticipants, [garbledSpan]);

        Assert.Equal(0, reAsrClient.Calls);                 // CT126 NEVER called — not a drain dep
        Assert.Single(result.Diffs);                        // only the glossary-backed term diff
        Assert.Equal("TotalEnergies", result.Diffs[0].After);
        Assert.Single(result.Omitted);                      // the garbled span is left as-is (omitted)
        Assert.Contains("re-ASR disabled", Assert.Single(result.Omitted).Reason);
    }

    // ── Re-ASR ENABLED but CT126 unreachable: the span is gracefully skipped, the drain continues ──
    [Fact]
    public async Task ReAsr_failure_is_gracefully_skipped_never_failing_the_drain()
    {
        const string baseText = "the [inaudible] figure";
        var garbledSpan = SpanOf(baseText, "[inaudible]");
        var throwing = new ThrowingReAsrClient();
        var proposals = new[]
        {
            new CorrectionCandidate(garbledSpan, "[inaudible]", ["the Q3"], CorrectionKind.Garbled, 0.7),
        };

        var stage = new CorrectionStage(
            new FakeCorrectionLlm(proposals), new InMemoryCorrectionMapStore(),
            throwing, new CorrectionGrader(PolicyPhase.GradedAutoApply), reAsrEnabled: true);

        // Does NOT throw — the CT126 failure is caught and the span omitted (never fails the drain).
        var result = await stage.CorrectAsync(Rec, baseText, NoParticipants, [garbledSpan]);

        Assert.Equal(1, throwing.Calls);
        Assert.Empty(result.Diffs);
        Assert.Single(result.Omitted);
        Assert.Contains("re-ASR unavailable", Assert.Single(result.Omitted).Reason);
    }

    // ── Scenario: Ambiguous name correction escalates ───────────────────────────────────────────
    [Fact]
    public async Task Ambiguous_name_correction_escalates_to_open_point()
    {
        const string baseText = "Ale will handle the filing";
        var span = SpanOf(baseText, "Ale");
        var proposals = new[]
        {
            new CorrectionCandidate(span, "Ale", ["Alessandro", "Alessia"], CorrectionKind.Name, 0.6),
        };
        // Two DISTINCT participants both match "Ale" → genuine ambiguity.
        var participants = new[]
        {
            new ResolvedParticipant("alessandro", "Alessandro", ["Ale"]),
            new ResolvedParticipant("alessia", "Alessia", ["Ale"]),
        };

        var result = await Stage(proposals).CorrectAsync(Rec, baseText, participants, NoGarbled);

        Assert.Empty(result.Diffs);
        var op = Assert.Single(result.OpenPoints);
        Assert.Equal(CorrectionOutcome.OpenPoint, op.Outcome);
        Assert.Equal(2, op.Candidates.Count);
        Assert.Contains("Alessandro", op.Candidates);
        Assert.Contains("Alessia", op.Candidates);
    }

    // ── Scenario: Operator answer feeds the correction map ──────────────────────────────────────
    [Fact]
    public async Task Operator_answer_feeds_the_correction_map_so_it_auto_corrects_next_time()
    {
        const string baseText = "the EBITDAR line looked healthy";
        var span = SpanOf(baseText, "EBITDAR");
        var proposals = new[]
        {
            new CorrectionCandidate(span, "EBITDAR", ["EBITDA"], CorrectionKind.Term, 0.8),
        };

        // First pass: term NOT yet in glossary → omitted (never guessed).
        var mapStore = new InMemoryCorrectionMapStore();
        var stage1 = new CorrectionStage(
            new FakeCorrectionLlm(proposals), mapStore, new FakeReAsrClient(),
            new CorrectionGrader(PolicyPhase.GradedAutoApply));
        var first = await stage1.CorrectAsync(Rec, baseText, NoParticipants, NoGarbled);
        Assert.Empty(first.Diffs);
        Assert.Single(first.Omitted);

        // Operator answers the correction point → the map learns it (human:// basis id recorded).
        await mapStore.UpsertAsync(new GlossaryEntry("EBITDAR", "EBITDA", CorrectionKind.Term, "op_answer_1"));

        // Second pass over the SAME store: now backed by the glossary → auto-corrects as a diff.
        var stage2 = new CorrectionStage(
            new FakeCorrectionLlm(proposals), mapStore, new FakeReAsrClient(),
            new CorrectionGrader(PolicyPhase.GradedAutoApply));
        var second = await stage2.CorrectAsync(Rec, baseText, NoParticipants, NoGarbled);
        var diff = Assert.Single(second.Diffs);
        Assert.Equal("EBITDA", diff.After);
    }

    // ── Escalate-only phase gate: a fully-backed correction is still withheld until validated ───
    [Fact]
    public async Task Escalate_only_phase_withholds_even_a_fully_backed_correction()
    {
        const string baseText = "the Total Energies deal closed";
        var span = SpanOf(baseText, "Total Energies");
        var proposals = new[]
        {
            new CorrectionCandidate(span, "Total Energies", ["TotalEnergies"], CorrectionKind.Term, 0.99),
        };
        var glossary = new[] { new GlossaryEntry("Total Energies", "TotalEnergies", CorrectionKind.Term) };

        var result = await Stage(proposals, glossary, phase: PolicyPhase.EscalateOnly)
            .CorrectAsync(Rec, baseText, NoParticipants, NoGarbled);

        Assert.Empty(result.Diffs); // NOT auto-applied while escalate-only
        Assert.Single(result.OpenPoints);
    }

    private static TextSpan SpanOf(string text, string substring)
    {
        var i = text.IndexOf(substring, StringComparison.Ordinal);
        if (i < 0) throw new ArgumentException($"'{substring}' not in base");
        return new TextSpan(i, i + substring.Length);
    }
}
