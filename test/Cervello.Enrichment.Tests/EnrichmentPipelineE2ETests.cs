using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Pipeline;
using Cervello.Enrichment.Pipeline.Stages;
using Cervello.Enrichment.Policy;
using Cervello.Enrichment.Ports;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// THE HEADLINE end-to-end test (MISSION E-PIPE). It drives a SYNTHETIC recording through the WHOLE
/// <see cref="EnrichmentPipeline"/> orchestrator — ingest → transcribe → diarize+embed → cluster-merge
/// → attribute → correct → enrich+link → apply — against FAKES (no network, no DB, no real audio),
/// and asserts the true end-to-end proof:
///
/// <list type="number">
/// <item>a <c>normalized</c> recording flows all the way to <c>graph_pr_opened</c>, advancing the
///   SCHEMAS §5 state at each transition;</item>
/// <item>a valid SCHEMAS §6 bundle is produced (all attributions <c>needs_confirmation</c>);</item>
/// <item>ambiguous facts ESCALATE to open-points and the map-PR is DRY-RUN (no real PR);</item>
/// <item>the NEVER-GUESS floor holds end-to-end — nothing unsourced/guessed reaches <c>map/</c>;</item>
/// <item>idempotent replay is a no-op; a stage error → <c>failed_retryable</c>; a contract violation
///   → <c>failed_terminal</c>; and escalate-only holds (a clean 0.90 match still escalates).</item>
/// </list>
///
/// Synthetic 192-d vectors + scripted fakes only — NO personal audio/data.
/// </summary>
public sealed class EnrichmentPipelineE2ETests
{
    private const string RecId = "20260704-standup";
    private static readonly byte[] SynthAudio = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

    private static RecordingRef Rec(bool ready = true) =>
        new(RecId, "sha-synthetic-aaa", "m4a", "fr", ready);

    // ── the synthetic diarize-embed response: three speakers (s1 clean, s2 ambiguous, s3 orphan) ──
    private static DiarizeEmbedResponse DiarizeResponse() => new(
        segments:
        [
            new DiarizedSegment("s1", 0, 5),
            new DiarizedSegment("s2", 5, 10),
            new DiarizedSegment("s3", 10, 15),
        ],
        embeddings:
        [
            new SpeakerEmbedding("s1", TestVectors.TiltedFromAxis(0, 5, 0.9)),   // → guilhem (clean)
            new SpeakerEmbedding("s2", TestVectors.TiltedFromAxis(30, 40, 0.95)),// → marco/mara (ambiguous)
            new SpeakerEmbedding("s3", TestVectors.Axis(99)),                    // orthogonal → omit
        ],
        model: new DiarizeEmbedModel("silero-vad", "ecapa-tdnn", 192));

    private static async Task<InMemoryVoiceprintStore> EnrolledStore()
    {
        var people = new (string slug, float[] vec)[]
        {
            ("guilhem", TestVectors.Axis(0)),
            ("marco", TestVectors.Axis(30)),
            ("mara", TestVectors.TiltedFromAxis(30, 31, 0.999)), // sits on top of marco → both ≥ auto
        };
        var store = new InMemoryVoiceprintStore(new EnrollmentAllowlist(people.Select(p => p.slug)));
        var enroll = new VoiceprintEnrollment(store);
        var day = new DateOnly(2026, 7, 4);
        foreach (var (slug, vec) in people)
            await enroll.EnrollOnConfirmationAsync(slug, vec, [$"rec://seed#{slug}"], null,
                ConfirmationBasis.Human($"seed-{slug}"), day);
        return store;
    }

    /// <summary>Build the orchestrator with all fakes. <paramref name="phase"/> selects escalate-only vs graded.</summary>
    private static async Task<(EnrichmentPipeline pipeline, FakeMapPrWriter pr, InMemoryOpenPointStore points, InMemoryEnrichmentLedger ledger)>
        BuildPipelineAsync(
            PolicyPhase phase = PolicyPhase.EscalateOnly,
            IDiarizeEmbedClient? diarizeClient = null,
            IReadOnlyList<CorrectionCandidate>? corrections = null,
            IGitPublisher? gitPublisher = null)
    {
        var ledger = new InMemoryEnrichmentLedger();
        var ingest = new IngestStage(ledger);

        var audio = new FakeAudioSource(SynthAudio);
        // Ratified base: the Google .txt IS the base (no CT126 in the drain path).
        var transcribe = new BaseTranscribeStage(
            new FakeBaseTranscriptSource(new BaseTranscript("standup transcript", "fr")),
            new InMemoryTranscriptStore());

        var diarize = new DiarizeEmbedStage(
            diarizeClient ?? FakeDiarizeEmbedClient.Returning(DiarizeResponse()));
        var merge = new ClusterMergeStage();

        var store = await EnrolledStore();
        var prior = new FilenameParticipantPriorSource(new Dictionary<string, PriorCandidates>
        {
            [RecId] = new PriorCandidates(["guilhem"], isStrong: true),
        });
        var attribution = new AttributionStage(store, prior, new DecisionPolicy(DecisionBands.Default, phase));

        var correction = new CorrectionStage(
            new FakeCorrectionLlm(corrections ?? Array.Empty<CorrectionCandidate>()),
            new InMemoryCorrectionMapStore(),
            new FakeReAsrClient(),
            new CorrectionGrader(phase));

        var enrich = new EnrichLinkStage(new FakeLinkResolver("guilhem", "marco", "mara"));

        var pr = new FakeMapPrWriter();
        var points = new InMemoryOpenPointStore();
        var graphWriter = new CervelloGraphWriter(pr, new FakeLinkResolver("guilhem", "marco", "mara"), new FakePinStore());
        var apply = new ApplyStage(graphWriter, points);

        var facts = new FakeRecordingFactSource();
        var pipeline = new EnrichmentPipeline(
            ingest, audio, transcribe, diarize, merge, attribution, correction, enrich, apply, facts,
            gitPublisher);
        return (pipeline, pr, points, ledger);
    }

    // ── THE HEADLINE: normalized → graph_pr_opened, bundle valid, escalations correct, never-guess ─
    [Fact]
    public async Task A_synthetic_recording_flows_end_to_end_to_graph_pr_opened_never_guessing()
    {
        var (pipeline, pr, points, _) = await BuildPipelineAsync(); // escalate-only (the default posture)

        var outcome = await pipeline.RunAsync(Rec(), EnrichmentState.Normalized);

        // 1. Reached graph_pr_opened, advancing the §5 state machine at EACH transition.
        Assert.Equal(PipelineStatus.Completed, outcome.Status);
        Assert.Equal(EnrichmentState.GraphPrOpened, outcome.State);
        Assert.Equal(
            new[]
            {
                EnrichmentState.Enriched,        // ingest
                EnrichmentState.AttentionScored, // attribution
                EnrichmentState.BundleCreated,   // enrich+link
                EnrichmentState.GraphPrOpened,   // apply
            },
            outcome.StateTransitions);

        // 2. A valid SCHEMAS §6 bundle — attributions ALWAYS needs_confirmation, no basis at bundle stage.
        var bundle = Assert.IsType<EnrichmentBundle>(outcome.Bundle);
        Assert.Equal(RecId, bundle.BundleId);
        Assert.Equal("recording", bundle.Kind);
        Assert.Equal(EnrichmentStateMachine.Name(EnrichmentState.BundleCreated), bundle.State);
        Assert.All(bundle.Enrichment.Attribution, a =>
        {
            Assert.Equal("needs_confirmation", a.Status);
            Assert.Null(a.Basis);
        });
        // The omitted (orphan) speaker s3 is NOT proposed as an attribution (never guessed a name).
        Assert.DoesNotContain(bundle.Enrichment.Attribution, a => a.Segment == "s3");

        // 3. Escalate-only holds: the ambiguous s2 + the below-reject s3 escalate; the clean 0.90 s1
        //    match STILL escalates under escalate-only. So all three speakers → open-points, NONE auto-applied.
        var apply = Assert.IsType<ApplyResult>(outcome.Apply);
        Assert.Equal(3, apply.OpenPoints);      // s1 (clean, escalated), s2 (ambiguous), s3 (below-reject)
        Assert.Equal(0, apply.Mutations);       // nothing auto-applied under escalate-only

        // 4. Map-PR is dry-run: no attribution mutation reached map/ (the ONLY mutations would be
        //    timeline lines from the fact source — here the fake proposes none, so no PR opens at all).
        Assert.Null(apply.Pr);
        Assert.Null(pr.LastPr);                 // no PR handed to the writer

        // 5. Never-guess floor end-to-end: whatever (if anything) reached map/ is sourced + basis'd.
        AssertNeverGuessed(pr.LastPr, new HashSet<string>(StringComparer.Ordinal) { "guilhem", "marco", "mara", RecId });

        // The open-points carry the ambiguity for the operator — the ONLY resolution path.
        Assert.Equal(3, (await points.ListPendingAsync()).Count);
    }

    // ── SEARCHABLE SUBSTRATE: a completed run PUBLISHES transcript + bundle + manifest to git ───────
    [Fact]
    public async Task A_completed_run_publishes_the_searchable_substrate_to_git()
    {
        var publisher = new FakeGitPublisher();
        var (pipeline, _, _, _) = await BuildPipelineAsync(gitPublisher: publisher);

        var outcome = await pipeline.RunAsync(Rec(), EnrichmentState.Normalized);

        Assert.Equal(PipelineStatus.Completed, outcome.Status);
        var request = Assert.Single(publisher.Requests);
        Assert.Equal(RecId, request.RecordingId);
        // The grounded, non-attribution artifacts the indexer needs — transcript + bundle + manifest.
        Assert.Contains($"recordings/transcripts/{RecId}.md", request.RepoRelativePaths);
        Assert.Contains($"inbox/{RecId}/data.json", request.RepoRelativePaths);
        Assert.Contains($"inbox/{RecId}/bundle.md", request.RepoRelativePaths);
        Assert.Contains("recordings/manifest.yaml", request.RepoRelativePaths);
        // LINT R7: no audio / voiceprint path is ever handed to the publisher.
        Assert.DoesNotContain(request.RepoRelativePaths, p =>
            p.Contains("audio", StringComparison.Ordinal) || p.Contains("voiceprint", StringComparison.Ordinal));
    }

    [Fact] // a git-publish FAILURE is NON-FATAL: the recording still drains to graph_pr_opened
    public async Task A_git_publish_failure_does_not_fail_the_drain()
    {
        var (pipeline, _, _, _) = await BuildPipelineAsync(gitPublisher: new ThrowingGitPublisher());

        var outcome = await pipeline.RunAsync(Rec(), EnrichmentState.Normalized);

        Assert.Equal(PipelineStatus.Completed, outcome.Status);
        Assert.Equal(EnrichmentState.GraphPrOpened, outcome.State); // reached the terminal state anyway
    }

    // ── RATIFIED: a Google-base recording drains FULLY to graph_pr_opened with NO CT126 anywhere ────
    [Fact]
    public async Task A_google_base_recording_drains_to_graph_pr_opened_without_ct126()
    {
        // Build the pipeline with NO CT126 transcribe client and NO re-ASR client wired at all — the
        // Google .txt is the base, re-ASR is disabled. This proves a full drain never depends on CT126.
        var ledger = new InMemoryEnrichmentLedger();
        var store = await EnrolledStore();
        var pr = new FakeMapPrWriter();
        var points = new InMemoryOpenPointStore();

        var pipeline = new EnrichmentPipeline(
            new IngestStage(ledger),
            new FakeAudioSource(SynthAudio),
            // Google .txt IS the base; NO ITranscribeClient supplied (CT126 truly absent).
            new BaseTranscribeStage(
                new FakeBaseTranscriptSource(new BaseTranscript("google base transcript", "fr")),
                new InMemoryTranscriptStore()),
            new DiarizeEmbedStage(FakeDiarizeEmbedClient.Returning(DiarizeResponse())),
            new ClusterMergeStage(),
            new AttributionStage(store,
                new FilenameParticipantPriorSource(new Dictionary<string, PriorCandidates> { [RecId] = new PriorCandidates(["guilhem"], true) }),
                new DecisionPolicy(DecisionBands.Default, PolicyPhase.EscalateOnly)),
            // A garbled span is present, but re-ASR is DISABLED and NO IReAsrClient is wired → the
            // span is left as-is (omitted), and the drain completes without CT126.
            new CorrectionStage(
                new FakeCorrectionLlm([new CorrectionCandidate(new TextSpan(0, 6), "google", ["Google"], CorrectionKind.Garbled, 0.7)]),
                new InMemoryCorrectionMapStore(),
                reAsr: null,                                  // CT126 re-ASR truly absent
                new CorrectionGrader(PolicyPhase.EscalateOnly)),
            new EnrichLinkStage(new FakeLinkResolver("guilhem", "marco", "mara")),
            new ApplyStage(new CervelloGraphWriter(pr, new FakeLinkResolver("guilhem", "marco", "mara"), new FakePinStore()), points),
            new FakeRecordingFactSource(garbledSpans: [new TextSpan(0, 6)]));

        var outcome = await pipeline.RunAsync(Rec(), EnrichmentState.Normalized);

        // Reached graph_pr_opened end-to-end WITHOUT any CT126 call (no client was even constructible).
        Assert.Equal(PipelineStatus.Completed, outcome.Status);
        Assert.Equal(EnrichmentState.GraphPrOpened, outcome.State);
        Assert.Contains(EnrichmentState.GraphPrOpened, outcome.StateTransitions);
        // Never-guess held: whatever reached map/ is sourced + basis'd.
        AssertNeverGuessed(pr.LastPr, new HashSet<string>(StringComparer.Ordinal) { "guilhem", "marco", "mara", RecId });
    }

    // ── ZERO diarize segments (e.g. a silent / synthetic verify WAV) is a VALID EMPTY result ────────
    // silero-VAD legitimately returns 0 speech spans for silence — the sidecar answers 200 with empty
    // segments+embeddings. That is NOT a failure: no speakers → no attributions (the never-guess floor),
    // the drain still advances all the way to graph_pr_opened. This is the graceful-degrade the tmpl-151
    // verify exercises; a 0-segment recording must never become failed_terminal.
    [Fact]
    public async Task Zero_diarize_segments_degrades_gracefully_to_graph_pr_opened_with_no_attributions()
    {
        var empty = new DiarizeEmbedResponse(
            segments: Array.Empty<DiarizedSegment>(),
            embeddings: Array.Empty<SpeakerEmbedding>(),
            model: new DiarizeEmbedModel("silero-vad", "ecapa-tdnn", 192));

        var (pipeline, pr, points, _) = await BuildPipelineAsync(
            diarizeClient: FakeDiarizeEmbedClient.Returning(empty));

        var outcome = await pipeline.RunAsync(Rec(), EnrichmentState.Normalized);

        // Reached graph_pr_opened — a valid empty result, NOT failed_terminal / failed_retryable.
        Assert.Equal(PipelineStatus.Completed, outcome.Status);
        Assert.Equal(EnrichmentState.GraphPrOpened, outcome.State);
        Assert.Contains(EnrichmentState.GraphPrOpened, outcome.StateTransitions);
        Assert.DoesNotContain(EnrichmentState.FailedTerminal, outcome.StateTransitions);
        Assert.DoesNotContain(EnrichmentState.FailedRetryable, outcome.StateTransitions);

        // No speakers → NO attributions (never guessed a name) and NO attribution mutations / open-points.
        var bundle = Assert.IsType<EnrichmentBundle>(outcome.Bundle);
        Assert.Empty(bundle.Enrichment.Attribution);
        var apply = Assert.IsType<ApplyResult>(outcome.Apply);
        Assert.Equal(0, apply.Mutations);       // nothing auto-applied (no clusters at all)
        Assert.Equal(0, apply.OpenPoints);      // no ambiguous speaker to escalate
        Assert.Empty(await points.ListPendingAsync());
        // Never-guess floor still holds over whatever (if anything) reached map/.
        AssertNeverGuessed(pr.LastPr, new HashSet<string>(StringComparer.Ordinal) { "guilhem", "marco", "mara", RecId });
    }

    // ── graded phase: the clean s1 auto-applies with a valid basis; s2/s3 still escalate ────────────
    [Fact]
    public async Task Under_graded_auto_apply_the_clean_match_reaches_map_with_a_valid_basis()
    {
        var (pipeline, pr, points, _) = await BuildPipelineAsync(PolicyPhase.GradedAutoApply);

        var outcome = await pipeline.RunAsync(Rec(), EnrichmentState.Normalized);

        Assert.Equal(EnrichmentState.GraphPrOpened, outcome.State);
        var apply = Assert.IsType<ApplyResult>(outcome.Apply);
        // s1 → guilhem auto-applied; s2 ambiguous + s3 below-reject → open-points.
        Assert.Equal(1, apply.Mutations);
        Assert.Equal(2, apply.OpenPoints);
        Assert.NotNull(apply.Pr); // a (fake) dry-run-analogue PR handle for the applied fact

        // The applied fact is sourced + basis'd + not invented (never-guess held even when writing).
        AssertNeverGuessed(pr.LastPr, new HashSet<string>(StringComparer.Ordinal) { "guilhem", "marco", "mara", RecId });
        var m = Assert.Single(pr.LastPr!.Mutations);
        Assert.Contains("guilhem", m.DossierPath);
        Assert.StartsWith("auto://", m.BasisId);
    }

    // ── idempotent replay: a claimed key re-run is a no-op (nothing re-processed) ────────────────────
    [Fact]
    public async Task Idempotent_replay_of_a_claimed_key_is_a_noop()
    {
        var (pipeline, pr, points, ledger) = await BuildPipelineAsync();
        // Pre-claim the key (as if a prior run already picked it up).
        Assert.True(await ledger.TryClaimAsync(Rec().IdempotencyKey));

        var outcome = await pipeline.RunAsync(Rec(), EnrichmentState.Normalized);

        Assert.True(outcome.IsReplay);
        Assert.Equal(PipelineStatus.Replayed, outcome.Status);
        Assert.Empty(outcome.StateTransitions);  // nothing advanced
        Assert.Null(outcome.Bundle);             // nothing produced
        Assert.Null(pr.LastPr);                  // nothing written
        Assert.Empty(await points.ListPendingAsync()); // nothing escalated
    }

    // ── a transient stage error → failed_retryable (retried under the same key) ─────────────────────
    [Fact]
    public async Task A_transient_stage_error_maps_to_failed_retryable()
    {
        var (pipeline, _, _, _) = await BuildPipelineAsync(
            diarizeClient: FakeDiarizeEmbedClient.Faulting(
                new DiarizeEmbedTransientException("sidecar 503")));

        var outcome = await pipeline.RunAsync(Rec(), EnrichmentState.Normalized);

        Assert.Equal(PipelineStatus.Failed, outcome.Status);
        Assert.Equal(EnrichmentState.FailedRetryable, outcome.State);
        Assert.Contains("sidecar 503", outcome.Reason);
        // It got past ingest (enriched) before failing at diarize — the transition trail records that.
        Assert.Contains(EnrichmentState.Enriched, outcome.StateTransitions);
        Assert.Equal(EnrichmentState.FailedRetryable, outcome.StateTransitions[^1]);
    }

    // ── a contract violation (undecodable audio) → failed_terminal ──────────────────────────────────
    [Fact]
    public async Task A_terminal_contract_violation_maps_to_failed_terminal()
    {
        var (pipeline, _, _, _) = await BuildPipelineAsync(
            diarizeClient: FakeDiarizeEmbedClient.Faulting(
                new DiarizeEmbedTerminalException("undecodable audio")));

        var outcome = await pipeline.RunAsync(Rec(), EnrichmentState.Normalized);

        Assert.Equal(PipelineStatus.Failed, outcome.Status);
        Assert.Equal(EnrichmentState.FailedTerminal, outcome.State);
        Assert.Contains("undecodable audio", outcome.Reason);
    }

    // ── missing audio → failed_terminal (no transcript/segments may be fabricated) ──────────────────
    [Fact]
    public async Task Missing_audio_maps_to_failed_terminal_never_fabricated()
    {
        var ledger = new InMemoryEnrichmentLedger();
        var store = await EnrolledStore();
        var pipeline = new EnrichmentPipeline(
            new IngestStage(ledger),
            new FakeAudioSource(unavailable: true),                    // the blob is gone
            new BaseTranscribeStage(new FakeBaseTranscriptSource(new BaseTranscript("x", "fr")), new InMemoryTranscriptStore()),
            new DiarizeEmbedStage(FakeDiarizeEmbedClient.Returning(DiarizeResponse())),
            new ClusterMergeStage(),
            new AttributionStage(store, new FilenameParticipantPriorSource(new Dictionary<string, PriorCandidates>()), new DecisionPolicy(DecisionBands.Default, PolicyPhase.EscalateOnly)),
            new CorrectionStage(new FakeCorrectionLlm(Array.Empty<CorrectionCandidate>()), new InMemoryCorrectionMapStore(), new FakeReAsrClient(), new CorrectionGrader(PolicyPhase.EscalateOnly)),
            new EnrichLinkStage(new FakeLinkResolver()),
            new ApplyStage(new CervelloGraphWriter(new FakeMapPrWriter(), new FakeLinkResolver(), new FakePinStore()), new InMemoryOpenPointStore()),
            new FakeRecordingFactSource());

        var outcome = await pipeline.RunAsync(Rec(), EnrichmentState.Normalized);

        Assert.Equal(EnrichmentState.FailedTerminal, outcome.State);
        Assert.Contains("audio unavailable", outcome.Reason);
    }

    // ── grounded timeline facts flow to the map-PR sourced + basis'd (never-guess with a write) ─────
    [Fact]
    public async Task Grounded_timeline_facts_reach_the_map_pr_sourced_and_based()
    {
        var (pipeline, pr, _, _) = await BuildPipelineWithTimelineAsync();

        var outcome = await pipeline.RunAsync(Rec(), EnrichmentState.Normalized);

        Assert.Equal(EnrichmentState.GraphPrOpened, outcome.State);
        Assert.NotNull(pr.LastPr);
        // A timeline mutation was written — sourced (R1) + basis'd (R9), not invented.
        Assert.Contains(pr.LastPr!.Mutations, m => m.DossierPath == "map/timeline.md");
        AssertNeverGuessed(pr.LastPr, new HashSet<string>(StringComparer.Ordinal)
            { "guilhem", "marco", "mara", RecId, "standup" });
    }

    private static async Task<(EnrichmentPipeline, FakeMapPrWriter, InMemoryOpenPointStore, InMemoryEnrichmentLedger)>
        BuildPipelineWithTimelineAsync()
    {
        var ledger = new InMemoryEnrichmentLedger();
        var store = await EnrolledStore();
        var pr = new FakeMapPrWriter();
        var points = new InMemoryOpenPointStore();
        var facts = new FakeRecordingFactSource(
            timeline: [new ProposedTimelineLine("2026-07-04", "standup happened", $"rec://{RecId}")]);
        var pipeline = new EnrichmentPipeline(
            new IngestStage(ledger),
            new FakeAudioSource(SynthAudio),
            new BaseTranscribeStage(new FakeBaseTranscriptSource(new BaseTranscript("standup transcript", "fr")), new InMemoryTranscriptStore()),
            new DiarizeEmbedStage(FakeDiarizeEmbedClient.Returning(DiarizeResponse())),
            new ClusterMergeStage(),
            new AttributionStage(store, new FilenameParticipantPriorSource(new Dictionary<string, PriorCandidates> { [RecId] = new PriorCandidates(["guilhem"], true) }), new DecisionPolicy(DecisionBands.Default, PolicyPhase.EscalateOnly)),
            new CorrectionStage(new FakeCorrectionLlm(Array.Empty<CorrectionCandidate>()), new InMemoryCorrectionMapStore(), new FakeReAsrClient(), new CorrectionGrader(PolicyPhase.EscalateOnly)),
            new EnrichLinkStage(new FakeLinkResolver("guilhem", "marco", "mara")),
            new ApplyStage(new CervelloGraphWriter(pr, new FakeLinkResolver("guilhem", "marco", "mara"), new FakePinStore()), points),
            facts);
        return (pipeline, pr, points, ledger);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // MIXED CASES — transcript-only + audio-only recordings (PIPELINE-MIXED-CASES)
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A TRANSCRIPT-ONLY recording: a Google .txt sha, NO audio (empty audioSha256).</summary>
    private static RecordingRef TranscriptOnlyRec() =>
        new("20260704-notes", audioSha256: "", "m4a", "fr", ready: true, googleTxtSha256: "txtsha-notes");

    /// <summary>An AUDIO-ONLY recording: an audio sha, NO Google .txt (null googleTxtSha256).</summary>
    private static RecordingRef AudioOnlyRec() =>
        new("20260704-voicememo", audioSha256: "sha-audio-only", "m4a", "fr", ready: true, googleTxtSha256: null);

    // ── TRANSCRIPT-ONLY: publishes the transcript + derives facts, SKIPS the audio stages, 0 attributions,
    //    and still advances to graph_pr_opened. No diarize/attribution is even ATTEMPTED (no audio). ─────
    [Fact]
    public async Task A_transcript_only_recording_publishes_and_skips_the_audio_stages_reaching_graph_pr_opened()
    {
        var ledger = new InMemoryEnrichmentLedger();
        var store = await EnrolledStore();
        var pr = new FakeMapPrWriter();
        var points = new InMemoryOpenPointStore();
        var publisher = new FakeGitPublisher();

        // An audio source that would THROW if fetched (proves it is never called for transcript-only),
        // and a diarize client that would THROW-if-empty if called (proves the audio stages are skipped).
        var audio = new FakeAudioSource(unavailable: true);
        var diarizeSpy = FakeDiarizeEmbedClient.Returning(DiarizeResponse());
        var transcriptStore = new InMemoryTranscriptStore();

        var pipeline = new EnrichmentPipeline(
            new IngestStage(ledger),
            audio,
            // The Google .txt IS the base (transcript-only) — read verbatim, published.
            new BaseTranscribeStage(new FakeBaseTranscriptSource(new BaseTranscript("meeting notes verbatim", "fr")), transcriptStore),
            new DiarizeEmbedStage(diarizeSpy),
            new ClusterMergeStage(),
            new AttributionStage(store, new FilenameParticipantPriorSource(new Dictionary<string, PriorCandidates>()), new DecisionPolicy(DecisionBands.Default, PolicyPhase.EscalateOnly)),
            new CorrectionStage(new FakeCorrectionLlm(Array.Empty<CorrectionCandidate>()), new InMemoryCorrectionMapStore(), new FakeReAsrClient(), new CorrectionGrader(PolicyPhase.EscalateOnly)),
            new EnrichLinkStage(new FakeLinkResolver()),
            new ApplyStage(new CervelloGraphWriter(pr, new FakeLinkResolver(), new FakePinStore()), points),
            new FakeRecordingFactSource(),
            publisher);

        var outcome = await pipeline.RunAsync(TranscriptOnlyRec(), EnrichmentState.Normalized);

        // Reached graph_pr_opened (NOT failed_terminal — a missing audio blob is not fabricated, it is
        // simply absent because the recording is transcript-only).
        Assert.Equal(PipelineStatus.Completed, outcome.Status);
        Assert.Equal(EnrichmentState.GraphPrOpened, outcome.State);
        Assert.DoesNotContain(EnrichmentState.FailedTerminal, outcome.StateTransitions);

        // The audio-dependent stages were SKIPPED: no audio fetch, no diarize call.
        Assert.Equal(0, audio.Fetches);
        Assert.Equal(0, diarizeSpy.Calls);

        // Zero speaker attributions (no audio ⇒ never guessed a speaker).
        var bundle = Assert.IsType<EnrichmentBundle>(outcome.Bundle);
        Assert.Empty(bundle.Enrichment.Attribution);
        var apply = Assert.IsType<ApplyResult>(outcome.Apply);
        Assert.Equal(0, apply.OpenPoints); // no speaker to escalate

        // The transcript was PUBLISHED (searchable substrate) — the Google .txt persisted verbatim.
        var req = Assert.Single(publisher.Requests);
        Assert.Contains($"recordings/transcripts/{req.RecordingId}.md", req.RepoRelativePaths);
        Assert.Equal("meeting notes verbatim", transcriptStore.Read(req.RecordingId)?.Markdown);
        // LINT R7: no audio/voiceprint path published.
        Assert.DoesNotContain(req.RepoRelativePaths, p =>
            p.Contains("audio", StringComparison.Ordinal) || p.Contains("voiceprint", StringComparison.Ordinal));
    }

    // ── AUDIO-ONLY: no Google .txt → BaseTranscribe transcribes via CT126 (BaseReTranscribeEnabled),
    //    THEN the normal audio pipeline (diarize → attribute) runs and it reaches graph_pr_opened. ───────
    [Fact]
    public async Task An_audio_only_recording_transcribes_via_ct126_then_diarizes_reaching_graph_pr_opened()
    {
        var ledger = new InMemoryEnrichmentLedger();
        var store = await EnrolledStore();
        var pr = new FakeMapPrWriter();
        var points = new InMemoryOpenPointStore();

        // No Google .txt (None) → the CT126 re-transcribe fallback produces the base. reTranscribeEnabled=true
        // is the config MC must set for audio-only (CERVELLO_BASE_RETRANSCRIBE_ENABLED=true).
        var ct126 = new FakeTranscribeClient(new BaseTranscript("ct126 transcribed base", "fr"));
        var diarizeSpy = FakeDiarizeEmbedClient.Returning(DiarizeResponse());
        var transcriptStore = new InMemoryTranscriptStore();

        var pipeline = new EnrichmentPipeline(
            new IngestStage(ledger),
            new FakeAudioSource(SynthAudio),
            new BaseTranscribeStage(
                FakeBaseTranscriptSource.None(),          // NO Google .txt for this recording
                transcriptStore,
                reTranscribe: ct126,                      // CT126 fallback wired …
                reTranscribeEnabled: true),               // … and ENABLED (audio-only requires it ON)
            new DiarizeEmbedStage(diarizeSpy),
            new ClusterMergeStage(),
            new AttributionStage(store,
                new FilenameParticipantPriorSource(new Dictionary<string, PriorCandidates> { ["20260704-voicememo"] = new PriorCandidates(["guilhem"], true) }),
                new DecisionPolicy(DecisionBands.Default, PolicyPhase.EscalateOnly)),
            new CorrectionStage(new FakeCorrectionLlm(Array.Empty<CorrectionCandidate>()), new InMemoryCorrectionMapStore(), new FakeReAsrClient(), new CorrectionGrader(PolicyPhase.EscalateOnly)),
            new EnrichLinkStage(new FakeLinkResolver("guilhem", "marco", "mara")),
            new ApplyStage(new CervelloGraphWriter(pr, new FakeLinkResolver("guilhem", "marco", "mara"), new FakePinStore()), points),
            new FakeRecordingFactSource());

        var outcome = await pipeline.RunAsync(AudioOnlyRec(), EnrichmentState.Normalized);

        Assert.Equal(PipelineStatus.Completed, outcome.Status);
        Assert.Equal(EnrichmentState.GraphPrOpened, outcome.State);

        // CT126 transcribed the base (no Google .txt) …
        Assert.Equal(1, ct126.Calls);
        Assert.Equal("ct126 transcribed base", transcriptStore.Read("20260704-voicememo")?.Markdown);
        // … and the audio pipeline ran (diarize was called — audio IS present).
        Assert.Equal(1, diarizeSpy.Calls);

        // Speakers were diarized + attributed (3 clusters → escalate-only → 3 open-points).
        var apply = Assert.IsType<ApplyResult>(outcome.Apply);
        Assert.Equal(3, apply.OpenPoints);
    }

    // ── Guard: an AUDIO recording whose blob is genuinely MISSING still fails terminal (unchanged) —
    //    the transcript-only skip must NOT mask a real audio-expected-but-absent failure. ───────────────
    [Fact]
    public async Task An_audio_recording_with_a_missing_blob_still_fails_terminal_not_skipped()
    {
        var ledger = new InMemoryEnrichmentLedger();
        var store = await EnrolledStore();
        var pipeline = new EnrichmentPipeline(
            new IngestStage(ledger),
            new FakeAudioSource(unavailable: true),       // audio EXPECTED (Rec has a sha) but the blob is gone
            new BaseTranscribeStage(new FakeBaseTranscriptSource(new BaseTranscript("x", "fr")), new InMemoryTranscriptStore()),
            new DiarizeEmbedStage(FakeDiarizeEmbedClient.Returning(DiarizeResponse())),
            new ClusterMergeStage(),
            new AttributionStage(store, new FilenameParticipantPriorSource(new Dictionary<string, PriorCandidates>()), new DecisionPolicy(DecisionBands.Default, PolicyPhase.EscalateOnly)),
            new CorrectionStage(new FakeCorrectionLlm(Array.Empty<CorrectionCandidate>()), new InMemoryCorrectionMapStore(), new FakeReAsrClient(), new CorrectionGrader(PolicyPhase.EscalateOnly)),
            new EnrichLinkStage(new FakeLinkResolver()),
            new ApplyStage(new CervelloGraphWriter(new FakeMapPrWriter(), new FakeLinkResolver(), new FakePinStore()), new InMemoryOpenPointStore()),
            new FakeRecordingFactSource());

        var outcome = await pipeline.RunAsync(Rec(), EnrichmentState.Normalized); // Rec() HAS an audio sha

        Assert.Equal(EnrichmentState.FailedTerminal, outcome.State);
        Assert.Contains("audio unavailable", outcome.Reason);
    }

    /// <summary>
    /// THE INVARIANT (reused from the E5 acceptance harness). For every mutation in an opened PR: a
    /// non-empty source, a parseable basis, and no fabricated value (every person/value is a known input).
    /// </summary>
    private static void AssertNeverGuessed(MapReviewPr? pr, ISet<string> knownInputs)
    {
        if (pr is null) return; // no PR = nothing asserted = trivially safe
        foreach (var m in pr.Mutations)
        {
            Assert.False(string.IsNullOrWhiteSpace(m.Source), "every applied fact must carry a source: ref (R1)");
            Assert.False(string.IsNullOrWhiteSpace(m.BasisId), "every applied fact must carry a basis (R9)");
            Assert.True(ConfirmationBasis.TryParse(m.BasisId, out _),
                $"basis '{m.BasisId}' must parse as auto://…@… or human://… (R9), never a bare/guessed claim");
            Assert.True(knownInputs.Any(k => m.Content.Contains(k, StringComparison.Ordinal) || m.DossierPath.Contains(k, StringComparison.Ordinal)),
                $"mutation '{m.Content}' references no known input — possible invention");
        }
    }
}
