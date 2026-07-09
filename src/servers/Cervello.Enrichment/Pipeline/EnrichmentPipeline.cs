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
/// <item><b>BaseTranscribe</b> (<see cref="BaseTranscribeStage"/>) — the recording's Google <c>.txt</c>
///   (the ratified base) → the immutable <see cref="BaseTranscript"/> (the correction substrate),
///   persisted once. CT126 is NOT called for the base (it is an optional, default-off fallback only).
///   Immediately after, an EARLY <c>baseOnly</c> <see cref="PublishSearchableSubstrateAsync"/> call
///   (M2 — decouple transcript-publish) pushes JUST the transcript, independent of diarize/attribution/
///   correction, so recall is fast — never gated behind the slower stages below.</item>
/// <item><b>DiarizeEmbed</b> (<see cref="DiarizeEmbedStage"/>) — the SAME audio → per-speaker
///   <see cref="SpeakerCluster"/>s (segments + centroid embeddings). A transient sidecar fault →
///   <c>failed_retryable</c>; a malformed/undecodable one → <c>failed_terminal</c>.</item>
/// <item><b>ClusterMerge</b> (<see cref="ClusterMergeStage"/>) — over-split clusters → one
///   <see cref="MergedCluster"/> per real speaker (the attribution unit). Immediately after, every
///   merged cluster's centroid is durably PERSISTED to the <see cref="IRecordingVoiceprintStore"/>
///   corpus (design <c>ste/cervello</c> <c>docs/design/autonomous-attribution.md</c> §4.1/§5 M3),
///   keyed by <c>(recordingId, clusterIndex)</c> — never the diarizer's per-recording label. This is
///   additive/best-effort: a persist failure never fails the drain.</item>
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
    private readonly IGitPublisher _gitPublisher;
    private readonly IRecordingVoiceprintStore _recordingVoiceprints;
    private readonly ITranscriptStore? _transcriptStore;
    private readonly AutoEnrollmentCoordinator? _autoEnroll;
    private readonly ILogger _log;

    /// <summary>The auto-basis version the applied-timeline mutations carry (lint R9 provenance).</summary>
    private const string TimelineBasisVersion = "v1";

    /// <summary>The recordings manifest git path (DESIGN §3) — republished so the indexer sees the roster.</summary>
    private const string ManifestPath = "recordings/manifest.yaml";

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
        IGitPublisher? gitPublisher = null,
        IRecordingVoiceprintStore? recordingVoiceprints = null,
        ITranscriptStore? transcriptStore = null,
        AutoEnrollmentCoordinator? autoEnroll = null,
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
        // Default to a no-op: a test that constructs the pipeline directly (no publisher) stays
        // offline; the live host wires the ForgejoContentsPublisher (searchable-substrate push).
        _gitPublisher = gitPublisher ?? new NoOpGitPublisher();
        // Default to an in-memory sibling: a test that constructs the pipeline directly without a
        // corpus store still exercises the persist call (offline), while the live host wires
        // PgRecordingVoiceprintStore (CT146 pgvector — the M3 corpus build spec).
        _recordingVoiceprints = recordingVoiceprints ?? new InMemoryRecordingVoiceprintStore();
        // M5: OPTIONAL — when a host wires it, the corrected + speaker-labeled document (§5 M5) is
        // persisted at recordings/attributions/<id>.md alongside the base transcript. When absent (a
        // test/host that doesn't need the artifact) the corrected+labeled build still runs (in-memory,
        // for the pipeline outcome / future callers) but nothing is persisted — never a hard dependency.
        _transcriptStore = transcriptStore;
        // M6 item 2: OPTIONAL — when a host wires it, an auto-applied participant-hint attribution
        // (GradedAutoApply + 1:1 + §10 allowlist) turns its enroll proposal into an actual voiceprint
        // write. When ABSENT (a test/host that doesn't need auto-enroll, or the default escalate-only
        // posture) the pipeline only LOGS proposals — nothing is ever written. Dark by default: the
        // coordinator itself no-ops under EscalateOnly even when wired.
        _autoEnroll = autoEnroll;
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
            // MIXED cases: a TRANSCRIPT-ONLY recording (recording.HasAudio == false) has NO audio blob.
            // We do NOT fetch it (there is nothing to fetch) and SKIP every audio-dependent stage
            // (diarize/merge/attribution) below — the transcript still publishes + derives facts, and
            // the state still advances to graph_pr_opened. When audio IS expected, an absent/empty blob
            // remains a terminal failure (never fabricate a transcript/segments from missing audio).
            var hasAudio = recording.HasAudio;
            ReadOnlyMemory<byte> audio = ReadOnlyMemory<byte>.Empty;
            if (hasAudio)
            {
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
            }
            else
            {
                _log.LogInformation(
                    "pipeline {Key}: transcript-only recording (no audio) — skipping diarize/merge/attribution; " +
                    "publishing transcript + deriving facts only",
                    recording.IdempotencyKey);
            }

            // ── 3. BASE: the Google .txt IS the base (ratified) → immutable base transcript ───────
            // The stage reads the recording's paired Google .txt verbatim (CT126 not called). CT126
            // base re-transcription is an optional, default-off fallback used only when no Google .txt
            // exists — so this step never depends on CT126 for a recording that carries a Google base.
            // For an AUDIO-ONLY recording (no Google .txt) the stage transcribes via CT126 when
            // BaseReTranscribeEnabled — else it yields NoBase (an empty substrate, never fabricated).
            var baseResult = await _transcribe.TranscribeAsync(recording, audio, ct).ConfigureAwait(false);
            // On a first run the stage returns the fresh base; on an idempotent re-run over a
            // pre-existing base it returns Transcript=null (the base is written once, never clobbered).
            // In the re-run case (and the rare NoBase case) we still need a substrate to feed
            // correction — the transcript store owns the persisted base; here we fall back to an
            // empty-substrate transcript in the recording's language so the chain composes (a live
            // re-run reads the persisted base; the base is never rewritten, nothing is fabricated).
            var baseTranscript = baseResult.Transcript ?? new BaseTranscript("", recording.Language);

            // ── 3b. EARLY PUBLISH: the base transcript, independent of diarize/attribution/correction ──
            //     (M2 — decouple transcript-publish for fast recall). Push JUST
            //     recordings/transcripts/<id>.md to the searchable substrate right after BaseTranscribe,
            //     so cervello_search / recall sees the recording within ~8s of transcription instead of
            //     waiting behind the full diarize→attribute→correct→apply chain (which can run minutes).
            //     Runs for EVERY recording that reaches this point — including transcript-only ones
            //     (audio stages 4-6 are skipped below, but the base still needs to be searchable fast).
            //     The stage-10 publish below re-publishes the FULL set (transcript + bundle + manifest)
            //     once enrichment completes — ForgejoContentsPublisher's create-or-update-by-sha makes
            //     the second push update the same blob in place, never a duplicate. Best-effort: never
            //     throws into the drain (see PublishSearchableSubstrateAsync).
            await PublishSearchableSubstrateAsync(recording.Id, bundleId: null, baseOnly: true, ct).ConfigureAwait(false);

            // ── 4-6. AUDIO STAGES (diarize → merge → attribute) — SKIPPED for a transcript-only ────
            // recording (no audio → no speakers → no attribution, the never-guess floor). Otherwise
            // run them normally; a 0-segment audio result already degrades gracefully (empty clusters
            // → empty verdicts). Either way there is NO speaker attribution when there is no audio.
            IReadOnlyList<AttributionVerdict> attribution;
            if (hasAudio)
            {
                // ── 4. DIARIZE + EMBED: the SAME audio → per-speaker clusters ─────────────────
                var diarize = await _diarize.DiarizeEmbedAsync(recording, audio, ct).ConfigureAwait(false);
                if (!diarize.Succeeded)
                    // The stage already classified transient (retryable) vs terminal — honour it verbatim.
                    return Fail(recording, state, diarize.FailureState!.Value, transitions,
                        diarize.Reason ?? "diarize-embed failed");

                // ── 5. CLUSTER MERGE: over-split clusters → one merged unit per real speaker ─────
                var merged = _merge.Merge(diarize.Clusters);

                // ── 5b. PERSIST THE CORPUS: every merged cluster's centroid → recording_voiceprints
                //     (design ste/cervello docs/design/autonomous-attribution.md §4.1/§5 M3). This is
                //     the raw material M4's cross-recording resolver clusters over — previously these
                //     centroids were transient (DiarizedCentroidEnrollmentSourceProvider, evicted after
                //     open-point resolution). Keyed on (recordingId, clusterIndex) — the merged-cluster
                //     LIST POSITION, never MergedCluster.MergedSpeaker (the diarizer's per-recording
                //     s1… label — not stable across recordings). Best-effort: a persist failure must
                //     NOT fail the drain (attribution below still runs off the in-memory `merged` list;
                //     the next run/replay re-persists under the idempotent upsert).
                try
                {
                    await _recordingVoiceprints.PersistAsync(
                        recording.Id, ToRecordingVoiceprints(recording.Id, merged, diarize.Model!), ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex,
                        "pipeline {Key}: recording-voiceprint corpus persist failed (non-fatal — " +
                        "attribution continues off the in-memory clusters; a replay re-persists)",
                        recording.IdempotencyKey);
                }

                // ── 6. ATTRIBUTION: merged clusters → enrolled-match / participant-hint / unknown verdicts
                //     + any auto-ENROLLMENT PROPOSALS (proposals only — the enroll write stays behind the
                //     escalate-only apply gate; M4 ships dark, the flip is M6).
                var attributionResult = await _attribution.ResolveAsync(recording.Id, merged, ct).ConfigureAwait(false);
                attribution = attributionResult.Verdicts;

                // ── 6b. AUTO-ENROLL (M6 item 2) — DARK BY DEFAULT ────────────────────────────────
                //     Turn a participant-hint enroll PROPOSAL into an actual voiceprint write, but ONLY
                //     under GradedAutoApply + the 1:1 auto-applied verdict + the §10 allowlist. When the
                //     coordinator is absent, or under the default EscalateOnly (it no-ops), NOTHING is
                //     written — the proposals stay proposals (logged). The write can never happen with
                //     the flag OFF. Off-allowlist persons are refused inside the coordinator (logged,
                //     never written), and any write failure is non-fatal (attribution already stands).
                if (attributionResult.EnrollmentProposals.Count > 0)
                {
                    var wrote = _autoEnroll is null
                        ? Array.Empty<string>()
                        : await _autoEnroll.AutoEnrollAsync(
                            recording.Id, attributionResult, DateOnly.FromDateTime(DateTime.UtcNow), ct)
                            .ConfigureAwait(false);

                    if (wrote.Count > 0)
                        _log.LogInformation(
                            "pipeline {Key}: {N} voiceprint(s) AUTO-ENROLLED (GradedAutoApply + 1:1 + allowlist): {People}",
                            recording.IdempotencyKey, wrote.Count, string.Join(", ", wrote));
                    else
                        _log.LogInformation(
                            "pipeline {Key}: {N} auto-enrollment PROPOSAL(s) from participant hints, NONE written " +
                            "(dark: escalate-only default / no coordinator / off-allowlist): {People}",
                            recording.IdempotencyKey, attributionResult.EnrollmentProposals.Count,
                            string.Join(", ", attributionResult.EnrollmentProposals.Select(p => p.PersonSlug)));
                }
            }
            else
            {
                // Transcript-only: no audio ⇒ no diarization ⇒ no attribution (never guessed a speaker).
                attribution = Array.Empty<AttributionVerdict>();
            }
            state = Advance(state, EnrichmentState.AttentionScored, transitions); // → attention_scored

            // Derived facts (summary/links/timeline/attention/participants/garbled) for THIS recording.
            // ── OPTIONAL-LLM step, GRACEFUL-DEGRADE. Fact-derivation is a Brain-API (CT139) Claude call
            //     that can 502 / time out on a larger transcript. It is an ENHANCEMENT, not a drain
            //     dependency: a failed derivation must NOT fail the recording (which would block the
            //     transcript from ever reaching git). On any transient/derivation failure we LOG a
            //     warning and CONTINUE with ZERO derived facts (no facts, no fact-derived links/
            //     timeline) — never guessed ones. The base transcript still publishes, the open-points
            //     from diarize/attribution are kept, and the run still reaches graph_pr_opened. (A
            //     cancellation still unwinds — that is a shutdown, not an LLM failure.)
            RecordingFacts facts;
            try
            {
                facts = await _facts.GetFactsAsync(recording.Id, baseTranscript, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "pipeline {Key}: fact-derivation unavailable ({Msg}) — continuing with ZERO derived facts " +
                    "(transcript still publishes, open-points kept; never guessed a fact)",
                    recording.IdempotencyKey, ex.Message);
                facts = RecordingFacts.None();
            }

            // ── 7. CORRECTION: base transcript + resolved participants → evidence-gated diffs ──────
            //     (base transcript from step 3 + participants derived alongside attribution — the
            //     "prior stages feed correction" hand-off.)
            // M5 — metadata-informed correction: the participant set handed to CorrectionStage is now
            // the UNION of (a) facts.Participants (the Brain-API-derived set) and (b) the M4
            // AttributionStage's OWN resolved persons (enrolled matches + participant-hint named
            // voices) — real names the attribution stage actually confirmed for THIS recording, not
            // just what the fact-derivation LLM guessed at. This closes the gap where a mis-transcribed
            // name matching an attributed speaker (e.g. "Gilan" → the attributed "Guilhem") had no
            // participant evidence to correct against. AttributionParticipants() below builds this
            // strictly from AutoApplied/Flagged AttributionVerdicts (never an OpenPoint/omitted one —
            // those are unconfirmed, never treated as an identity fact); glossary continues to come
            // from ICorrectionMapStore.GetGlossaryAsync() inside CorrectionStage (unchanged — M5 does
            // not touch the grader/evidence-gating, only the inputs it grounds against).
            var correctionParticipants = MergeParticipants(facts.Participants, AttributionParticipants(attribution));

            // OPTIONAL-LLM step, GRACEFUL-DEGRADE. Correction is another Brain-API (CT139) Claude call
            // that can 502 / time out. Like fact-derivation it ENHANCES the transcript, it does not gate
            // the drain: a failed correction pass must NOT fail the recording. On failure we LOG a warning
            // and CONTINUE with an EMPTY correction result — the base transcript is left as-is (never
            // rewritten, never guessed), and the run still reaches graph_pr_opened.
            CorrectionResult correction;
            try
            {
                correction = await _correction
                    .CorrectAsync(recording.Id, baseTranscript.Markdown, correctionParticipants, facts.GarbledSpans, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "pipeline {Key}: correction unavailable ({Msg}) — continuing with NO corrections " +
                    "(base transcript left as-is; drain continues)",
                    recording.IdempotencyKey, ex.Message);
                correction = new CorrectionResult(
                    baseTranscript.Markdown,
                    Array.Empty<CorrectionDiff>(),
                    Array.Empty<CorrectionVerdict>(),
                    Array.Empty<CorrectionVerdict>());
            }
            var correctionVerdicts = CollectCorrectionVerdicts(correction);

            // ── 7b. SPEAKER LABELS: apply the M4 attribution result to the corrected transcript ─────
            //     (design §5 M5 "apply speaker labels"). TranscriptLabeler is a pure, additive helper —
            //     NOT a new correction stage: it mechanically applies the ALREADY-GATED correction.Diffs
            //     to the base text (never re-decides a diff) and renders a roster strictly from the M4
            //     AttributionResult (real name for an applied verdict, "Unknown speaker N" for a
            //     LocalUnknownLabel verdict, NOTHING for an unconfirmed open-point or a plain omit — the
            //     never-invent floor). Best-effort persist: a store failure never fails the drain (same
            //     posture as the searchable-substrate publish below).
            var correctedText = TranscriptLabeler.ApplyDiffs(baseTranscript.Markdown, correction.Diffs);
            var roster = TranscriptLabeler.BuildRoster(new AttributionResult(attribution, Array.Empty<EnrollmentProposal>()));
            var labeledDocument = correctedText + TranscriptLabeler.RenderRosterSection(roster);
            if (_transcriptStore is not null)
            {
                try
                {
                    await _transcriptStore.WriteAttributionAsync(recording.Id, labeledDocument, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex,
                        "pipeline {Key}: corrected+labeled transcript persist failed (non-fatal — " +
                        "drain continues; a replay/rescan re-persists)",
                        recording.IdempotencyKey);
                }
            }

            // ── 8. ENRICH + LINK: verdicts + derived facts → SCHEMAS §6 bundle ──────────────
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

            // ── 9. APPLY / GRAPH-ADD: route graded facts (dry-run PR / open-points / omit) ────
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

            // ── 10. PUBLISH the SEARCHABLE SUBSTRATE to ste/cervello git (independent of the map-PR
            //     dry-run gate). The verbatim transcript + the enrichment bundle + the manifest are
            //     grounded, NON-attribution artifacts (git paths per DESIGN §3); pushing them lets the
            //     strictly-git-sourced indexer (:8009) index the recording so recall returns real
            //     content. Audio + voiceprints NEVER enter git (LINT R7 — enforced in the publisher).
            //     Best-effort: a push failure is logged but does NOT fail the drain or roll back state
            //     (the recording is already fully enriched on-CT; the next run/rescan re-publishes).
            await PublishSearchableSubstrateAsync(recording.Id, bundleId, baseOnly: false, ct).ConfigureAwait(false);

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

    /// <summary>
    /// Publish this recording's grounded, non-attribution artifacts to <c>ste/cervello</c> git so the
    /// indexer can index them. When <paramref name="baseOnly"/> is <see langword="true"/> (the M2 EARLY
    /// publish, called right after <see cref="BaseTranscribeStage"/> — before diarize/attribution/
    /// correction have run) only the verbatim transcript (<c>recordings/transcripts/&lt;id&gt;.md</c>)
    /// is pushed, so recall is fast and never gated behind the slower audio/LLM stages; <paramref
    /// name="bundleId"/> is not needed in that case. Otherwise (the stage-10 ENRICHED publish, after
    /// <c>graph_pr_opened</c>) the full set is pushed: the (immutable, write-once) base transcript path
    /// — a second push over the SAME path, updating the blob the early call created rather than
    /// duplicating it — the M5 corrected + speaker-labeled document
    /// (<c>recordings/attributions/&lt;id&gt;.md</c>, when a transcript store is wired), the enrichment
    /// bundle (<c>inbox/&lt;id&gt;/{data.json,bundle.md}</c>), and the manifest
    /// (<c>recordings/manifest.yaml</c>). <see cref="ForgejoContentsPublisher"/> is a GET-sha →
    /// POST(create)/PUT(update)-by-sha adapter, so the two calls over the SAME transcript path are
    /// idempotent by construction: the first call creates (no prior sha), the second reads the sha the
    /// first call left and updates in place — never a conflict, never a duplicate. The publisher reads
    /// each path from the CT working tree, refuses any never-git path (audio/voiceprints, LINT R7), and
    /// is a no-op for an unchanged/absent file. Best-effort: never throws into the drain (searchability
    /// is decoupled from the enrich state).
    /// </summary>
    private async Task PublishSearchableSubstrateAsync(
        string recordingId, string? bundleId, bool baseOnly, CancellationToken ct)
    {
        try
        {
            var paths = new List<string> { $"recordings/transcripts/{recordingId}.md" };
            if (!baseOnly)
            {
                // M5: the corrected + speaker-labeled document, only when a transcript store persisted
                // one (offline pipelines with no store wired have nothing at this path to push).
                if (_transcriptStore is not null)
                    paths.Add(_transcriptStore.AttributionPath(recordingId));
                paths.Add($"inbox/{bundleId}/data.json");
                paths.Add($"inbox/{bundleId}/bundle.md");
                paths.Add(ManifestPath);
            }
            var result = await _gitPublisher
                .PublishAsync(new GitPublishRequest(recordingId, paths), ct)
                .ConfigureAwait(false);
            if (!result.WasNoOp)
                _log.LogInformation(
                    "pipeline {Rec}: published {Kind} searchable substrate ({Count} file(s)) → indexer will re-index",
                    recordingId, baseOnly ? "base-only" : "enriched", result.Pushed.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The recording is already fully enriched + its artifacts written on-CT; a failed push
            // must NOT fail the drain or roll back §5 state. The next run/60-min rescan re-publishes.
            _log.LogWarning(ex,
                "pipeline {Rec}: searchable-substrate publish failed (non-fatal — will retry next run/rescan)",
                recordingId);
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
    /// M5 — the M4 attribution stage's OWN resolved persons, as <see cref="ResolvedParticipant"/>s the
    /// correction stage can evidence-gate a name-correction against. Built ONLY from applied verdicts
    /// (<see cref="AttributionOutcome.AutoApplied"/>/<see cref="AttributionOutcome.Flagged"/>, carrying
    /// a non-null <see cref="AttributionVerdict.Person"/>) — an <c>OpenPoint</c> (unconfirmed) or a
    /// plain <c>Omitted</c> contributes nothing (the never-invent floor: an unconfirmed attribution is
    /// never treated as a participant fact). A resolved participant here carries no confirmed alias
    /// list of its own (the attribution stage names the canonical person, not a spelling variant), so
    /// <see cref="ResolvedParticipant.Matches"/> falls back to matching the canonical name only —
    /// correction of a mis-transcribed spelling of that name still routes through the merge with
    /// <paramref name="attribution"/>'s sibling <c>facts.Participants</c> aliases when present.
    /// </summary>
    private static IReadOnlyList<ResolvedParticipant> AttributionParticipants(
        IReadOnlyList<AttributionVerdict> attribution)
    {
        var result = new List<ResolvedParticipant>();
        foreach (var v in attribution)
        {
            if (v.IsApplied && v.Person is not null)
                result.Add(new ResolvedParticipant(v.Person, v.Person, Array.Empty<string>()));
        }
        return result;
    }

    /// <summary>
    /// M5 — the participant set handed to <see cref="Pipeline.Stages.CorrectionStage"/>: the UNION of
    /// the Brain-API-derived <paramref name="factsParticipants"/> and the M4-attribution-derived
    /// <paramref name="attributionParticipants"/>, deduplicated by slug (an attribution-derived entry
    /// never overrides a facts-derived one that already names the same person — it only ADDS people the
    /// attribution stage confirmed but the fact-derivation pass did not surface, e.g. an enrolled voice
    /// match with no corresponding participant-hint metadata).
    /// </summary>
    private static IReadOnlyList<ResolvedParticipant> MergeParticipants(
        IReadOnlyList<ResolvedParticipant> factsParticipants,
        IReadOnlyList<ResolvedParticipant> attributionParticipants)
    {
        if (attributionParticipants.Count == 0)
            return factsParticipants; // no attribution-derived names — nothing to add (common case)

        var merged = new List<ResolvedParticipant>(factsParticipants);
        var known = new HashSet<string>(factsParticipants.Select(p => p.Slug), StringComparer.Ordinal);
        foreach (var p in attributionParticipants)
            if (known.Add(p.Slug))
                merged.Add(p);
        return merged;
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

    /// <summary>
    /// Project this recording's deterministically-ordered merged clusters (<see cref="ClusterMerge"/>
    /// sorts by <c>MergedSpeaker</c>) into <see cref="RecordingVoiceprint"/> rows keyed by their LIST
    /// POSITION (<c>ClusterIndex</c>) — never <c>MergedCluster.MergedSpeaker</c> (design §4.1: the
    /// diarizer's per-recording label is not stable across recordings). The label is still carried as
    /// descriptive metadata (<see cref="RecordingVoiceprint.MergedSpeakerLabel"/>). The sidecar's
    /// reported embed model id (<see cref="DiarizeEmbedModel.Embed"/>) is recorded per row for
    /// space-compatibility gating (design §4.1 <c>model</c> column) — the SAME response that produced
    /// these clusters, never a hard-coded/guessed identifier.
    /// </summary>
    private static IReadOnlyList<RecordingVoiceprint> ToRecordingVoiceprints(
        string recordingId, IReadOnlyList<MergedCluster> merged, DiarizeEmbedModel model)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = new List<RecordingVoiceprint>(merged.Count);
        for (var i = 0; i < merged.Count; i++)
        {
            var cluster = merged[i];
            var duration = cluster.Segments.Sum(s => s.End - s.Start);
            rows.Add(new RecordingVoiceprint(
                recordingId: recordingId,
                clusterIndex: i,
                centroid: cluster.Centroid,
                model: model.Embed,
                segmentCount: cluster.Segments.Count,
                durationSeconds: duration,
                mergedSpeakerLabel: cluster.MergedSpeaker,
                createdAt: now));
        }
        return rows;
    }

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
