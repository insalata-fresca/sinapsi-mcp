using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Pipeline;
using Cervello.Enrichment.Pipeline.Stages;
using Cervello.Enrichment.Policy;
using Cervello.Enrichment.Ports;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// MISSION M5 — metadata-informed transcript correction + apply speaker labels (design
/// <c>ste/cervello</c> <c>docs/design/autonomous-attribution.md</c> §5 M5). Pipeline-level proof, over
/// the SAME <see cref="EnrichmentPipeline"/> orchestrator the E-PIPE headline test drives, that:
///
/// <list type="number">
/// <item>the M4 <see cref="AttributionStage"/>'s OWN resolved persons (enrolled + participant-hint
///   named voices) are folded into the participant set <see cref="CorrectionStage"/> evidence-gates
///   against — NOT just the Brain-API-derived <c>facts.Participants</c>;</item>
/// <item>the corrected + speaker-labeled document persists via <see cref="ITranscriptStore"/>: real
///   names where a voice was attributed, "Unknown speaker N" where not, and NEVER a name for an
///   unconfirmed open-point (the never-invent floor, proven negatively);</item>
/// <item>the existing correction/grader machinery is REUSED unchanged — an unbacked candidate is still
///   OMITTED, never invented.</item>
/// </list>
///
/// Synthetic 256-d vectors + scripted fakes only — no personal audio/data.
/// </summary>
public sealed class M5CorrectionAttributionWiringTests
{
    private const string RecId = "20260709-standup";
    private static readonly byte[] SynthAudio = [1, 2, 3, 4, 5, 6, 7, 8];

    private static RecordingRef Rec() => new(RecId, "sha-synthetic-m5", "m4a", "fr", ready: true);

    // One clean enrolled voice (s1 → guilhem) + one unmatched/unhinted voice (s2 → local unknown).
    private static DiarizeEmbedResponse DiarizeResponse() => new(
        segments:
        [
            new DiarizedSegment("s1", 0, 5),
            new DiarizedSegment("s2", 5, 10),
        ],
        embeddings:
        [
            new SpeakerEmbedding("s1", TestVectors.TiltedFromAxis(0, 5, 0.9)), // → guilhem (clean, auto band)
            new SpeakerEmbedding("s2", TestVectors.Axis(99)),                   // orthogonal → no match, no hint
        ],
        model: new DiarizeEmbedModel("silero-vad", "pyannote/wespeaker-voxceleb-resnet34-LM", 256));

    private static async Task<InMemoryVoiceprintStore> EnrolledStore()
    {
        var store = new InMemoryVoiceprintStore(new EnrollmentAllowlist(["guilhem"]));
        var enroll = new VoiceprintEnrollment(store);
        await enroll.EnrollOnConfirmationAsync(
            "guilhem", TestVectors.Axis(0), ["rec://seed#guilhem"], null,
            ConfirmationBasis.Human("seed-guilhem"), new DateOnly(2026, 7, 9));
        return store;
    }

    // ── WIRING: the M4 attribution stage's OWN resolved person (guilhem, enrolled — NOT surfaced by
    // ── the fact source) is folded into the participant set CorrectionStage evidence-gates against, so
    // ── a mis-transcribed spelling of that name corrects as a diff, backed by the [[guilhem]] evidence
    // ── ref (a participant match), even though the fact source proposed ZERO participants. ────────────
    [Fact]
    public async Task Correction_context_includes_the_attribution_resolved_person_not_just_fact_source_participants()
    {
        const string baseText = "Gilan will send the numbers";
        var span = SpanOf(baseText, "Gilan");
        var candidates = new[]
        {
            new CorrectionCandidate(span, "Gilan", ["guilhem"], CorrectionKind.Name, 0.85),
        };
        var correctionLlm = new FakeCorrectionLlm(candidates);

        var (pipeline, _, _, transcriptStore) = await BuildAsync(
            phase: PolicyPhase.GradedAutoApply,
            correctionLlm: correctionLlm,
            baseText: baseText,
            // The fact source proposes NO participants — the ONLY source of "guilhem" as a resolved
            // participant is the attribution stage's own enrolled match on s1.
            factsParticipants: []);

        var outcome = await pipeline.RunAsync(Rec(), EnrichmentState.Normalized);

        Assert.Equal(PipelineStatus.Completed, outcome.Status);
        // The correction LLM WAS handed a non-empty participant set carrying the attribution-resolved
        // person — proving the wiring reaches CorrectionStage, not just the bundle/apply attribution.
        Assert.NotNull(correctionLlm.LastContext);
        Assert.Contains(correctionLlm.LastContext!.Participants, p => p.Slug == "guilhem");
        _ = transcriptStore;
    }

    // ── HEADLINE: real name applied where attributed, "Unknown speaker N" where not, corrected term ───
    // ── applied as a diff — all via the REUSED, unmodified CorrectionStage/CorrectionGrader. ──────────
    [Fact]
    public async Task Labeled_transcript_carries_real_names_and_unknown_labels_from_attribution()
    {
        const string baseText = "we reviewed the Total Energies contract today";
        var termSpan = SpanOf(baseText, "Total Energies");
        var candidates = new[]
        {
            new CorrectionCandidate(termSpan, "Total Energies", ["TotalEnergies"], CorrectionKind.Term, 0.9),
        };
        var glossary = new[] { new GlossaryEntry("Total Energies", "TotalEnergies", CorrectionKind.Term) };

        var (pipeline, _, _, transcriptStore) = await BuildAsync(
            phase: PolicyPhase.GradedAutoApply,
            correctionLlm: new FakeCorrectionLlm(candidates),
            baseText: baseText,
            glossary: glossary);

        var outcome = await pipeline.RunAsync(Rec(), EnrichmentState.Normalized);

        Assert.Equal(PipelineStatus.Completed, outcome.Status);
        var doc = transcriptStore.ReadAttribution(RecId);
        Assert.NotNull(doc);
        // The evidence-gated term correction was applied to the persisted document (a diff, not a
        // wholesale rewrite — the rest of the sentence is untouched).
        Assert.Contains("we reviewed the TotalEnergies contract today", doc);
        // The roster names the ENROLLED, attributed voice (s1 → guilhem) with its real name …
        Assert.Contains("s1 — guilhem", doc);
        // … and labels the unmatched/unhinted voice (s2) with the deterministic local-unknown handle —
        // never a fabricated name.
        Assert.Contains("s2 — Unknown speaker", doc);
    }

    // ── NEVER-INVENT NEGATIVE #1: an unbacked correction candidate is OMITTED — the base text is left
    // ── as-is in the persisted labeled document too (proves the reused grader's gate still holds all
    // ── the way through to the M5 output, not just CorrectionResult). ──────────────────────────────
    [Fact]
    public async Task Unbacked_correction_candidate_is_omitted_never_applied_to_the_labeled_document()
    {
        const string baseText = "the xzzybat metric was flat";
        var span = SpanOf(baseText, "xzzybat");
        // No glossary entry, no participant, no re-ASR — nothing backs this "fix".
        var candidates = new[]
        {
            new CorrectionCandidate(span, "xzzybat", ["XyzBot"], CorrectionKind.Garbled, 0.95),
        };

        var (pipeline, _, _, transcriptStore) = await BuildAsync(
            phase: PolicyPhase.GradedAutoApply,
            correctionLlm: new FakeCorrectionLlm(candidates),
            baseText: baseText);

        var outcome = await pipeline.RunAsync(Rec(), EnrichmentState.Normalized);

        Assert.Equal(PipelineStatus.Completed, outcome.Status);
        var doc = transcriptStore.ReadAttribution(RecId);
        Assert.NotNull(doc);
        // The unbacked "fix" never made it into the document — the base substring survives verbatim.
        Assert.Contains("the xzzybat metric was flat", doc);
        Assert.DoesNotContain("XyzBot", doc);
    }

    // ── NEVER-INVENT NEGATIVE #2: under escalate-only, s1's clean enrolled match is WITHHELD to an
    // ── open-point (not yet confirmed) — the labeled document must NOT name s1 "guilhem" until the
    // ── operator confirms it. Only the local-unknown s2 gets a label; s1 gets none. ──────────────────
    [Fact]
    public async Task Escalate_only_withholds_the_name_from_the_labeled_document_until_confirmed()
    {
        const string baseText = "standup notes";
        var (pipeline, _, _, transcriptStore) = await BuildAsync(
            phase: PolicyPhase.EscalateOnly, // the default posture — even a clean match escalates
            correctionLlm: new FakeCorrectionLlm(Array.Empty<CorrectionCandidate>()),
            baseText: baseText);

        var outcome = await pipeline.RunAsync(Rec(), EnrichmentState.Normalized);

        Assert.Equal(PipelineStatus.Completed, outcome.Status);
        var doc = transcriptStore.ReadAttribution(RecId);
        Assert.NotNull(doc);
        // s1's enrolled match is withheld to an open-point under escalate-only — NEVER named "guilhem"
        // in the document until the operator answers (the roster has no line for an open-point).
        Assert.DoesNotContain("guilhem", doc);
        Assert.DoesNotContain("s1 —", doc);
        // s2 (no match, no hint) still gets its deterministic local-unknown label — that path is
        // independent of the escalate-only gate (it was never an identity claim to begin with).
        Assert.Contains("s2 — Unknown speaker", doc);
    }

    // ── Build a fully-wired pipeline (real stages, in-memory adapters) with the M5 transcriptStore
    // ── param supplied, so the labeled document actually persists and can be asserted on. ────────────
    private static async Task<(EnrichmentPipeline pipeline, FakeMapPrWriter pr, InMemoryOpenPointStore points, InMemoryTranscriptStore transcriptStore)>
        BuildAsync(
            PolicyPhase phase,
            ICorrectionLlm correctionLlm,
            string baseText,
            IReadOnlyList<GlossaryEntry>? glossary = null,
            IReadOnlyList<ResolvedParticipant>? factsParticipants = null)
    {
        var ledger = new InMemoryEnrichmentLedger();
        var ingest = new IngestStage(ledger);
        var audio = new FakeAudioSource(SynthAudio);
        var transcriptStore = new InMemoryTranscriptStore();
        var transcribe = new BaseTranscribeStage(
            new FakeBaseTranscriptSource(new BaseTranscript(baseText, "fr")), transcriptStore);

        var diarize = new DiarizeEmbedStage(FakeDiarizeEmbedClient.Returning(DiarizeResponse()));
        var merge = new ClusterMergeStage();

        var voiceStore = await EnrolledStore();
        var attribution = new AttributionStage(voiceStore, new DecisionPolicy(DecisionBands.Default, phase));

        var correction = new CorrectionStage(
            correctionLlm, new InMemoryCorrectionMapStore(glossary), new FakeReAsrClient(),
            new CorrectionGrader(phase));

        var enrich = new EnrichLinkStage(new FakeLinkResolver("guilhem"));
        var pr = new FakeMapPrWriter();
        var points = new InMemoryOpenPointStore();
        var graphWriter = new CervelloGraphWriter(pr, new FakeLinkResolver("guilhem"), new FakePinStore());
        var apply = new ApplyStage(graphWriter, points);

        var facts = new FakeRecordingFactSource(participants: factsParticipants ?? []);
        var pipeline = new EnrichmentPipeline(
            ingest, audio, transcribe, diarize, merge, attribution, correction, enrich, apply, facts,
            gitPublisher: null, recordingVoiceprints: null, transcriptStore: transcriptStore);
        return (pipeline, pr, points, transcriptStore);
    }

    private static TextSpan SpanOf(string text, string substring)
    {
        var i = text.IndexOf(substring, StringComparison.Ordinal);
        if (i < 0) throw new ArgumentException($"'{substring}' not in base");
        return new TextSpan(i, i + substring.Length);
    }
}
