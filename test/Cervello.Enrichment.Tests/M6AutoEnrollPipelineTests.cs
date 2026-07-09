using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Pipeline;
using Cervello.Enrichment.Pipeline.Stages;
using Cervello.Enrichment.Policy;
using Cervello.Enrichment.Ports;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// MISSION M6 — auto-enroll wired END-TO-END through the real <see cref="EnrichmentPipeline"/> (the same
/// orchestrator the drain runs). Proves the write happens ONLY under GradedAutoApply + a 1:1 hint + the
/// §10 allowlist, and NEVER under the default EscalateOnly — over the full pipeline, not just the unit
/// coordinator. A recording with one enrolled voice (guilhem) + one hinted-but-unenrolled voice (marco):
/// under GradedAutoApply marco's voiceprint is auto-enrolled from his voice cluster; under EscalateOnly
/// nothing is written. Synthetic vectors + in-memory adapters only.
/// </summary>
public sealed class M6AutoEnrollPipelineTests
{
    private const string RecId = "20260709-standup";
    private static readonly byte[] SynthAudio = [1, 2, 3, 4, 5, 6, 7, 8];

    private static RecordingRef Rec() => new(RecId, "sha-synthetic-m6", "m4a", "fr", ready: true);

    // s1 → guilhem (enrolled, clean auto-band match); s2 → marco (orthogonal → unmatched → 1:1 hint).
    private static DiarizeEmbedResponse DiarizeResponse() => new(
        segments: [new DiarizedSegment("s1", 0, 5), new DiarizedSegment("s2", 5, 10)],
        embeddings:
        [
            new SpeakerEmbedding("s1", TestVectors.TiltedFromAxis(0, 5, 0.9)),
            new SpeakerEmbedding("s2", TestVectors.Axis(30)),
        ],
        model: new DiarizeEmbedModel("silero-vad", "ecapa-tdnn", 192));

    // The STORE's §10 allowlist is the union of everyone consented — always includes guilhem (so the
    // test can SEED his enrolled voiceprint) plus whatever the coordinator is allowed to auto-enroll.
    private static async Task<InMemoryVoiceprintStore> EnrolledStore(EnrollmentAllowlist coordinatorAllowlist)
    {
        var storeAllowlist = new EnrollmentAllowlist(coordinatorAllowlist.AllowedSlugs.Append("guilhem"));
        var store = new InMemoryVoiceprintStore(storeAllowlist);
        var enroll = new VoiceprintEnrollment(store);
        await enroll.EnrollOnConfirmationAsync(
            "guilhem", TestVectors.Axis(0), ["rec://seed#guilhem"], null,
            ConfirmationBasis.Human("seed-guilhem"), new DateOnly(2026, 7, 9));
        return store;
    }

    private static async Task<(EnrichmentPipeline pipeline, InMemoryVoiceprintStore store)> BuildAsync(
        PolicyPhase phase, EnrollmentAllowlist allowlist)
    {
        var ledger = new InMemoryEnrichmentLedger();
        var ingest = new IngestStage(ledger);
        var audio = new FakeAudioSource(SynthAudio);
        var transcriptStore = new InMemoryTranscriptStore();
        var transcribe = new BaseTranscribeStage(
            new FakeBaseTranscriptSource(new BaseTranscript("standup notes", "fr")), transcriptStore);
        var diarize = new DiarizeEmbedStage(FakeDiarizeEmbedClient.Returning(DiarizeResponse()));
        var merge = new ClusterMergeStage();

        var store = await EnrolledStore(allowlist);
        var attribution = new AttributionStage(
            store, new DecisionPolicy(DecisionBands.Default, phase),
            new InMemoryParticipantHintSource(new Dictionary<string, IReadOnlyList<string>>
            {
                [RecId] = ["guilhem", "marco"], // guilhem accounted by voice; marco is the 1:1 hint
            }));

        var correction = new CorrectionStage(
            new FakeCorrectionLlm(Array.Empty<CorrectionCandidate>()),
            new InMemoryCorrectionMapStore(), new FakeReAsrClient(), new CorrectionGrader(phase));
        var enrich = new EnrichLinkStage(new FakeLinkResolver("guilhem", "marco"));
        var graphWriter = new CervelloGraphWriter(new FakeMapPrWriter(), new FakeLinkResolver("guilhem", "marco"), new FakePinStore());
        var apply = new ApplyStage(graphWriter, new InMemoryOpenPointStore());
        var facts = new FakeRecordingFactSource(participants: []);

        // M6: the auto-enroll coordinator wired at the SAME phase — dark under EscalateOnly.
        var coord = new AutoEnrollmentCoordinator(new VoiceprintEnrollment(store), allowlist, phase);

        var pipeline = new EnrichmentPipeline(
            ingest, audio, transcribe, diarize, merge, attribution, correction, enrich, apply, facts,
            gitPublisher: null, recordingVoiceprints: null, transcriptStore: transcriptStore, autoEnroll: coord);
        return (pipeline, store);
    }

    [Fact] // under the DEFAULT EscalateOnly the full pipeline writes NO voiceprint (dark).
    public async Task Pipeline_escalate_only_writes_no_voiceprint()
    {
        var (pipeline, store) = await BuildAsync(PolicyPhase.EscalateOnly, new EnrollmentAllowlist(["marco"]));

        var outcome = await pipeline.RunAsync(Rec(), EnrichmentState.Normalized);

        Assert.Equal(PipelineStatus.Completed, outcome.Status);
        Assert.Null(await store.GetAsync("marco")); // marco was NEVER auto-enrolled under the default
    }

    [Fact] // under a SIMULATED GradedAutoApply + the allowlist the full pipeline AUTO-ENROLLS marco's
           //  voiceprint from his voice cluster — the flip working end-to-end.
    public async Task Pipeline_graded_auto_enrolls_the_hinted_voice()
    {
        var (pipeline, store) = await BuildAsync(PolicyPhase.GradedAutoApply, new EnrollmentAllowlist(["marco"]));

        var outcome = await pipeline.RunAsync(Rec(), EnrichmentState.Normalized);

        Assert.Equal(PipelineStatus.Completed, outcome.Status);
        var print = await store.GetAsync("marco");
        Assert.NotNull(print); // marco WAS auto-enrolled
        // …from the CORRECT voice (s2 = Axis 30): a match of that voice now returns marco ≥ auto band.
        var back = await store.MatchAsync(TestVectors.Axis(30));
        Assert.Equal("marco", back[0].PersonSlug);
        Assert.True(back[0].Cosine >= DecisionBands.DefaultAutoBand);
    }

    [Fact] // under GradedAutoApply but OFF the allowlist, the full pipeline REFUSES the enroll (never
           //  written) and still completes the drain — the §10 gate holds through the pipeline.
    public async Task Pipeline_graded_off_allowlist_refuses_the_enroll_but_completes()
    {
        var (pipeline, store) = await BuildAsync(PolicyPhase.GradedAutoApply, EnrollmentAllowlist.Empty);

        var outcome = await pipeline.RunAsync(Rec(), EnrichmentState.Normalized);

        Assert.Equal(PipelineStatus.Completed, outcome.Status); // drain not broken
        Assert.Null(await store.GetAsync("marco"));             // refused — not on the allowlist
    }
}
