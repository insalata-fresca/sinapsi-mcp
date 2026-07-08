using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Pipeline.Stages;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cervello.Enrichment.Pipeline;

/// <summary>
/// The END-TO-END enrichment ORCHESTRATOR — the piece E-HOST (#62) deliberately deferred. It runs a
/// <c>normalized</c> recording through ALL stages in order, threading each stage's output into the
/// next and ADVANCING the SCHEMAS §5 state at every transition, until the recording reaches
/// <c>graph_pr_opened</c> (a dry-run map-PR opened) or its facts all escalate to open-points.
///
/// <para><b>The stage chain + data-flow contract</b> (each row is "output → the next stage's input"):
/// <list type="number">
/// <item><b>Ingest</b> (<see cref="IngestStage"/>) — CLAIMS the §8 idempotency key (a replay is a
///   logged no-op) and advances <c>normalized → enriched</c>. This is the idempotency gate: the
///   whole run below only happens on a fresh key.</item>
/// <item><b>Fetch audio</b> (<see cref="IAudioSource"/>) — the transient bytes both audio stages
///   consume, fetched ONCE and threaded to both (never persisted git-side).</item>
/// <item><b>BaseTranscribe</b> (<see cref="BaseTranscribeStage"/>) — audio → the immutable
///   <see cref="BaseTranscript"/> (the correction substrate), persisted once.</item>
/// <item><b>DiarizeEmbed</b> (<see cref="DiarizeEmbedStage"/>) — the SAME audio → per-speaker
///   <see cref="SpeakerCluster"/>s (segments + centroid embeddings). A transient sidecar fault →
///   <c>failed_retryable</c>; a malformed/undecodable one → <c>failed_terminal</c>.</item>
/// <item><b>ClusterMerge</b> (<see cref="ClusterMergeStage"/>) — over-split clusters → one
///   <see cref="MergedCluster"/> per real speaker (the attribution unit).</item>
/// <item><b>Attribution</b> (<see cref="AttributionStage"/>) — merged clusters + the org-chart prior
///   + enrolled voiceprints → <see cref="AttributionVerdict"/>s (auto/flag/open/omit). This is the
///   diarize→attribution artifact hand-off: the merged-cluster CENTROID (from step 5) is what the
///   attribution stage cosine-matches. → advance <c>enriched → attention_scored</c>.</item>
/// <item><b>Correction</b> (<see cref="CorrectionStage"/>) — the base transcript (step 3) + the
///   resolved participants → evidence-gated correction diffs / open-points / omits. The
///   base-transcript + attribution-derived participants are the "prior stages feed correction"
///   artifact hand-off.</item>
/// <item><b>EnrichLink</b> (<see cref="EnrichLinkStage"/>) — attribution verdicts + derived facts →
///   a SCHEMAS §6 <see cref="EnrichmentBundle"/> (all attributions <c>needs_confirmation</c>). →
///   advance <c>attention_scored → bundle_created</c>.</item>
/// <item><b>Apply</b> (<see cref="ApplyStage"/>) — attribution + correction verdicts + proposed
///   timeline lines → the map-PR (DRY-RUN by default) / open-points / omits. → advance
///   <c>bundle_created → graph_pr_opened</c>.</item>
/// </list></para>
///
/// <para><b>Grounded-autonomy posture (unchanged, enforced here).</b> The orchestrator NEVER
/// re-decides: the escalate-only phase gate lives in the <see cref="Policy.DecisionPolicy"/> the
/// attribution/correction stages already carry (in the first concept every band → open-point), and
/// the map-PR dry-run lives in the <see cref="IMapPrWriter"/> adapter. A clean 0.90 match still
/// escalates under escalate-only. Nothing unsourced/guessed can reach <c>map/</c> — the apply +
/// graph-writer self-lint enforce it.</para>
///
/// <para><b>Idempotency + failure.</b> A recording already past <c>normalized</c> (or whose key is
/// claimed) short-circuits — replay is a no-op (<see cref="PipelineOutcome.Replayed"/>). A
/// per-stage throw → <c>failed_retryable</c> with the reason (retried under the same key). A stage
/// contract violation (undecodable audio, malformed sidecar response) → <c>failed_terminal</c>. The
/// orchestrator NEVER advances backward and NEVER invents a state (validated by
/// <see cref="EnrichmentStateMachine"/>).</para>
/// </summary>
public sealed class EnrichmentPipeline
{
    private readonly IngestStage _ingest;
    private readonly IAudioSource _audio;
    private readonly BaseTranscribeStage _transcribe;
    private readonly DiarizeEmbedStage _diarize;
    private readonly ClusterMergeStage _merge;
    private readonly AttributionStage _attribution;
    private readonly CorrectionStage _correction;
    private readonly EnrichLinkStage _enrich;
    private readonly ApplyStage _apply;
    private readonly IRecordingFactSource _facts;
    private readonly ILogger _log;

    /// <summary>The auto-basis version the applied-timeline mutations carry (lint R9 provenance).</summary>
    private const string TimelineBasisVersion = "v1";

    public EnrichmentPipeline(
        IngestStage ingest,
        IAudioSource audio,
        BaseTranscribeStage transcribe,
        DiarizeEmbedStage diarize,
        ClusterMergeStage merge,
        AttributionStage attribution,
        CorrectionStage correction,
        EnrichLinkStage enrich,
        ApplyStage apply,
        IRecordingFactSource facts,
        ILogger<EnrichmentPipeline>? logger = null)
    {
        _ingest = ingest ?? throw new ArgumentNullException(nameof(ingest));
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _transcribe = transcribe ?? throw new ArgumentNullException(nameof(transcribe));
        _diarize = diarize ?? throw new ArgumentNullException(nameof(diarize));
        _merge = merge ?? throw new ArgumentNullException(nameof(merge));
        _attribution = attribution ?? throw new ArgumentNullException(nameof(attribution));
        _correction = correction ?? throw new ArgumentNullException(nameof(correction));
        _enrich = enrich ?? throw new ArgumentNullException(nameof(enrich));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _facts = facts ?? throw new ArgumentNullException(nameof(facts));
        _log = logger ?? NullLogger<EnrichmentPipeline>.Instance;
    }

    /// <summary>
    /// Run one recording through the whole chain from <paramref name="currentState"/> (expected
    /// <c>normalized</c>). Returns the outcome: the terminal state reached, whether it was a replay,
    /// the bundle produced, the apply result, and any failure reason. State is advanced through
    /// <see cref="PipelineOutcome.StateTransitions"/> — the host persists each to the shared row.
    /// </summary>
    public async Task<PipelineOutcome> RunAsync(
        RecordingRef recording, EnrichmentState currentState, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recording);
        var transitions = new List<EnrichmentState>();

        try
        {
            // ── 1. INGEST: claim the §8 key (replay → no-op) + advance normalized → enriched ──────
            var ingest = await _ingest.IngestAsync(recording, currentState, ct).ConfigureAwait(false);
            if (ingest.IsReplay)
            {
                _log.LogInformation("pipeline {Key}: replay — no-op (idempotency key already claimed)",
                    recording.IdempotencyKey);
                return PipelineOutcome.Replay(currentState);
            }
            if (!ingest.PickedUp)
            {
                _log.LogDebug("pipeline {Key}: not eligible ({Reason})", recording.IdempotencyKey, ingest.Reason);
                return PipelineOutcome.NotEligible(currentState, ingest.Reason ?? "not eligible");
            }
            var state = Advance(currentState, ingest.State, transitions); // → enriched

            // ── 2. FETCH AUDIO (once; threaded to transcribe + diarize) ───────────────────────────
            ReadOnlyMemory<byte> audio;
            try
            {
                audio = await _audio.FetchAsync(recording.Id, recording.AudioSha256, ct).ConfigureAwait(false);
            }
            catch (AudioUnavailableException ex)
            {
                return Fail(recording, state, EnrichmentState.FailedTerminal, transitions,
                    $"audio unavailable: {ex.Message}");
            }
            if (audio.IsEmpty)
                return Fail(recording, state, EnrichmentState.FailedTerminal, transitions,
                    "audio source returned empty bytes (no transcript/segments may be fabricated)");

            // ── 3. BASE TRANSCRIBE: audio → immutable base transcript ─────────────────────────────
            var baseResult = await _transcribe.TranscribeAsync(recording, audio, ct).ConfigureAwait(false);
            // On a first run the stage returns the fresh transcript; on an idempotent re-run over a
            // pre-existing base it returns Transcript=null (the base is written once, never clobbered).
            // In the re-run case we still need the substrate to feed correction — the transcript store
            // owns it; here we fall back to an empty-substrate transcript in the recording's language
            // so the chain composes (a live re-run reads the persisted base; the base is never rewritten).
            var baseTranscript = baseResult.Transcript ?? new BaseTranscript("", recording.Language);

            // ── 4. DIARIZE + EMBED: the SAME audio → per-speaker clusters ─────────────────────────
            var diarize = await _diarize.DiarizeEmbedAsync(recording, audio, ct).ConfigureAwait(false);
            if (!diarize.Succeeded)
                // The stage already classified transient (retryable) vs terminal — honour it verbatim.
                return Fail(recording, state, diarize.FailureState!.Value, transitions,
                    diarize.Reason ?? "diarize-embed failed");

            // ── 5. CLUSTER MERGE: over-split clusters → one merged unit per real speaker ───────────
            var merged = _merge.Merge(diarize.Clusters);

            // ── 6. ATTRIBUTION: merged clusters (centroids) → grounded verdicts ───────────────────
            var attribution = await _attribution.ResolveAsync(recording.Id, merged, ct).ConfigureAwait(false);
            state = Advance(state, EnrichmentState.AttentionScored, transitions); // → attention_scored

            // Derived facts (summary/links/timeline/attention/participants/garbled) for THIS recording.
            var facts = await _facts.GetFactsAsync(recording.Id, baseTranscript, ct).ConfigureAwait(false);

            // ── 7. CORRECTION: base transcript + resolved participants → evidence-gated diffs ──────
            //     (base transcript from step 3 + participants derived alongside attribution — the
            //     "prior stages feed correction" hand-off.)
            var correction = await _correction
                .CorrectAsync(recording.Id, baseTranscript.Markdown, facts.Participants, facts.GarbledSpans, ct)
                .ConfigureAwait(false);
            var correctionVerdicts = CollectCorrectionVerdicts(correction);

            // ── 8. ENRICH + LINK: verdicts + derived facts → SCHEMAS §6 bundle ────────────────────
            var bundleId = recording.Id;
            var enrichInput = new EnrichLinkInput(
                bundleId: bundleId,
                sourceRef: $"rec://{recording.Id}",
                idempotencyKey: recording.IdempotencyKey,
                kind: "recording",
                createdAt: DateTimeOffset.UtcNow.ToString("O"),
                summary: facts.Summary,
                entities: facts.Entities,
                dates: facts.Dates,
                proposedLinks: facts.ProposedLinks,
                proposedTimeline: facts.ProposedTimeline,
                attribution: attribution,
                attention: facts.Attention);
            var bundle = await _enrich.EnrichAsync(enrichInput, ct).ConfigureAwait(false);
            state = Advance(state, EnrichmentState.BundleCreated, transitions); // → bundle_created

            // ── 9. APPLY / GRAPH-ADD: route graded facts (dry-run PR / open-points / omit) ────────
            var applyInput = new ApplyInput(
                bundleId: bundleId,
                recordingId: recording.Id,
                date: FirstDate(facts),
                attributions: attribution,
                corrections: correctionVerdicts,
                timelineMutations: BuildTimelineMutations(recording, bundleId, facts),
                referencedLinks: BuildReferencedLinks(facts));
            var applied = await _apply.ApplyAsync(applyInput, ct).ConfigureAwait(false);
            state = Advance(state, EnrichmentState.GraphPrOpened, transitions); // → graph_pr_opened

            _log.LogInformation(
                "pipeline {Key}: normalized → graph_pr_opened ({Mut} mutations, {Open} open-points, {Omit} omitted, pr={Pr})",
                recording.IdempotencyKey, applied.Mutations, applied.OpenPoints, applied.Omitted,
                applied.Pr?.Branch ?? "(none — escalate-only)");

            return PipelineOutcome.Completed(state, transitions, bundle, applied);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // shutdown — let the caller unwind
        }
        catch (Exception ex)
        {
            // Any other throw for THIS recording is transient → failed_retryable (retried under the
            // same idempotency key). Never invent a state; never crash the drain on one item.
            var from = transitions.Count > 0 ? transitions[^1] : currentState;
            _log.LogError(ex, "pipeline {Key}: failed → failed_retryable", recording.IdempotencyKey);
            transitions.Add(EnrichmentState.FailedRetryable);
            return PipelineOutcome.Failed(EnrichmentState.FailedRetryable, transitions, ex.Message);
        }
    }

    /// <summary>Validate + record a forward transition (SCHEMAS §5); returns the new state.</summary>
    private static EnrichmentState Advance(
        EnrichmentState from, EnrichmentState to, List<EnrichmentState> transitions)
    {
        EnrichmentStateMachine.EnsureLegal(from, to);
        transitions.Add(to);
        return to;
    }

    private PipelineOutcome Fail(
        RecordingRef recording, EnrichmentState from, EnrichmentState failState,
        List<EnrichmentState> transitions, string reason)
    {
        // failed_retryable / failed_terminal are legal from any live spine state (SCHEMAS §5).
        EnrichmentStateMachine.EnsureLegal(from, failState, reason);
        transitions.Add(failState);
        _log.LogWarning("pipeline {Key}: {From} → {To} ({Reason})",
            recording.IdempotencyKey, EnrichmentStateMachine.Name(from),
            EnrichmentStateMachine.Name(failState), reason);
        return PipelineOutcome.Failed(failState, transitions, reason);
    }

    /// <summary>Flatten the correction result's applied/open/omitted verdicts for the apply stage.</summary>
    private static IReadOnlyList<CorrectionVerdict> CollectCorrectionVerdicts(CorrectionResult correction)
    {
        var all = new List<CorrectionVerdict>(
            correction.Diffs.Count + correction.OpenPoints.Count + correction.Omitted.Count);
        foreach (var d in correction.Diffs) all.Add(CorrectionVerdict.AutoApplied(d));
        all.AddRange(correction.OpenPoints);
        all.AddRange(correction.Omitted);
        return all;
    }

    /// <summary>
    /// Project the derived (already R1-sourced) proposed timeline lines into map mutations the apply
    /// stage writes to <c>map/timeline.md</c>. Each carries its source + an auto-basis (R1/R9). In
    /// escalate-only these still flow (they are grounded, non-attribution facts); the map-PR itself
    /// is dry-run by config so no real PR opens.
    /// </summary>
    private static IReadOnlyList<MapMutation> BuildTimelineMutations(
        RecordingRef recording, string bundleId, RecordingFacts facts)
    {
        var basis = ConfirmationBasis.Auto(TimelineBasisVersion, "timeline").Id;
        var mutations = new List<MapMutation>(facts.ProposedTimeline.Count);
        foreach (var line in facts.ProposedTimeline)
            mutations.Add(new MapMutation(
                dossierPath: "map/timeline.md",
                section: "## Timeline",
                content: line.ToTimelineLine(),
                source: line.Source,
                confidence: 1.0,
                bundleId: bundleId,
                basisId: basis));
        return mutations;
    }

    /// <summary>The links the timeline mutations reference (for R4 stub declaration at apply).</summary>
    private static IReadOnlyList<ReferencedLink> BuildReferencedLinks(RecordingFacts facts) =>
        facts.ProposedLinks.Select(l => new ReferencedLink(l.Slug, "person")).ToList();

    private static string FirstDate(RecordingFacts facts) =>
        facts.Dates.Count > 0 ? facts.Dates[0] : DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
}

/// <summary>
/// The outcome of one end-to-end pipeline run: the state reached, whether it was a replay/ineligible,
/// the ordered §5 transitions (the host persists each to the shared row), the produced bundle (when
/// completed), the apply result, and any failure reason.
/// </summary>
public sealed record PipelineOutcome
{
    private PipelineOutcome(
        PipelineStatus status,
        EnrichmentState state,
        IReadOnlyList<EnrichmentState> transitions,
        EnrichmentBundle? bundle,
        ApplyResult? apply,
        string? reason)
    {
        Status = status;
        State = state;
        StateTransitions = transitions;
        Bundle = bundle;
        Apply = apply;
        Reason = reason;
    }

    public PipelineStatus Status { get; }

    /// <summary>The terminal state this run reached (persisted to the shared row).</summary>
    public EnrichmentState State { get; }

    /// <summary>The ordered forward transitions taken this run (each advanced the shared §5 row).</summary>
    public IReadOnlyList<EnrichmentState> StateTransitions { get; }

    /// <summary>The SCHEMAS §6 bundle produced (present only on <see cref="PipelineStatus.Completed"/>).</summary>
    public EnrichmentBundle? Bundle { get; }

    /// <summary>The apply result: PR handle (null under escalate-only/dry-run), open-point + omit counts.</summary>
    public ApplyResult? Apply { get; }

    /// <summary>The failure reason (present only on a failed/ineligible outcome).</summary>
    public string? Reason { get; }

    public bool IsReplay => Status == PipelineStatus.Replayed;
    public bool IsCompleted => Status == PipelineStatus.Completed;

    internal static PipelineOutcome Completed(
        EnrichmentState state, IReadOnlyList<EnrichmentState> transitions,
        EnrichmentBundle bundle, ApplyResult apply) =>
        new(PipelineStatus.Completed, state, transitions, bundle, apply, null);

    internal static PipelineOutcome Replay(EnrichmentState state) =>
        new(PipelineStatus.Replayed, state, Array.Empty<EnrichmentState>(), null, null, "replay");

    internal static PipelineOutcome NotEligible(EnrichmentState state, string reason) =>
        new(PipelineStatus.NotEligible, state, Array.Empty<EnrichmentState>(), null, null, reason);

    internal static PipelineOutcome Failed(
        EnrichmentState state, IReadOnlyList<EnrichmentState> transitions, string reason) =>
        new(PipelineStatus.Failed, state, transitions, null, null, reason);
}

/// <summary>The kind of a <see cref="PipelineOutcome"/>.</summary>
public enum PipelineStatus
{
    /// <summary>Ran the full chain to <c>graph_pr_opened</c> (or escalate-only equivalent).</summary>
    Completed,

    /// <summary>The idempotency key was already claimed — a logged no-op (nothing re-run).</summary>
    Replayed,

    /// <summary>The recording was not in an eligible (<c>normalized</c>+ready) state — left untouched.</summary>
    NotEligible,

    /// <summary>A stage failed → <c>failed_retryable</c> / <c>failed_terminal</c> (reason set).</summary>
    Failed,
}
