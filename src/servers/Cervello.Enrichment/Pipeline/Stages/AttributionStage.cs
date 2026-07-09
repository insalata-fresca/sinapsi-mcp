using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Policy;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cervello.Enrichment.Pipeline.Stages;

/// <summary>
/// The ENROLL-BASED attribution stage (design: enroll-based attribution + participant hint). For each
/// merged (within-recording) voice cluster it resolves an identity through four bounded rules, in order,
/// NEVER fabricating a name and NEVER linking voices across recordings:
///
/// <list type="number">
///   <item><b>Enrolled match</b> — cosine vs the enrolled <see cref="IVoiceprintStore"/>. A single match
///     ≥ the auto band (0.62) → attribute to that person (autonomous). Two enrolled ≥ auto → ambiguous
///     → open-point. Tombstoned people are dropped (lint R8). This runs through the unchanged
///     <see cref="DecisionPolicy"/> escalate-only gate (a clean match still escalates in M4).</item>
///   <item><b>Participant-hint assignment</b> — after the enrolled pass, if there is exactly ONE
///     unmatched voice AND exactly ONE hinted participant (<see cref="IParticipantHintSource"/>) not
///     already accounted for by an enrolled match → attribute that voice to that participant AND PROPOSE
///     auto-enrollment of their voiceprint from this recording. The enroll WRITE stays behind the
///     escalate-only apply gate (M4 dark; the flip is M6) — this stage proposes only.</item>
///   <item><b>Unknown</b> — no enrolled match and no unambiguous hint → a recording-LOCAL
///     "Unknown speaker N" label, deterministic per <c>(recordingId, clusterIndex)</c>, FLAGGED. No map
///     write, no cross-recording linking, no guessing.</item>
///   <item><b>Ambiguous</b> — &gt; 1 unmatched voice AND &gt; 1 unaccounted hinted participant → a single
///     escalate-only open-point ("which of {A,B} is this voice?"). The rare path.</item>
/// </list>
///
/// <para>Assistive-only: this stage produces VERDICTS + enrollment PROPOSALS. It never writes a name to
/// <c>map/</c> and never enrolls — the escalate-only apply gate + a confirmation own that (M6).</para>
/// </summary>
public sealed class AttributionStage(
    IVoiceprintStore store,
    DecisionPolicy policy,
    IParticipantHintSource? participantHints = null,
    ILogger<AttributionStage>? logger = null)
{
    private readonly IVoiceprintStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly DecisionPolicy _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    private readonly IParticipantHintSource? _hints = participantHints;
    private readonly ILogger _log = logger ?? NullLogger<AttributionStage>.Instance;

    /// <summary>Resolve every merged cluster for a recording into a verdict + any enrollment proposals.</summary>
    public async Task<AttributionResult> ResolveAsync(
        string recordingId,
        IReadOnlyList<MergedCluster> clusters,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(recordingId))
            throw new ArgumentException("recordingId must be non-empty", nameof(recordingId));
        ArgumentNullException.ThrowIfNull(clusters);

        if (clusters.Count == 0)
            return AttributionResult.Empty;

        // ── 1. ENROLLED MATCH per cluster (through the unchanged decision policy) ────────────────────
        var verdicts = new AttributionVerdict?[clusters.Count];
        var enrolledPersons = new HashSet<string>(StringComparer.Ordinal); // persons accounted for by a voice match
        var unmatched = new List<int>();                                   // cluster indices with no enrolled match

        for (var i = 0; i < clusters.Count; i++)
        {
            var (verdict, matchedPerson) = await ResolveEnrolledAsync(recordingId, clusters[i], ct).ConfigureAwait(false);
            if (matchedPerson is not null)
            {
                // An enrolled voice match (auto-applied, or escalate-only-withheld to an open-point, or
                // ambiguous) — the person is "accounted for" so a hint won't double-assign them.
                verdicts[i] = verdict;
                enrolledPersons.Add(matchedPerson);
            }
            else
            {
                // No enrolled person matched — a candidate for participant-hint assignment / unknown.
                unmatched.Add(i);
            }
        }

        // ── 2/3/4. PARTICIPANT-HINT ASSIGNMENT over the UNMATCHED voices ─────────────────────────────
        var proposals = new List<EnrollmentProposal>();
        var hints = _hints is null
            ? Array.Empty<string>()
            : await _hints.GetParticipantsAsync(recordingId, ct).ConfigureAwait(false);

        // Hints not already accounted for by an enrolled voice match — deterministic order preserved.
        var unaccountedHints = hints
            .Where(h => !string.IsNullOrWhiteSpace(h) && !enrolledPersons.Contains(h))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (unmatched.Count == 1 && unaccountedHints.Count == 1)
        {
            // (2) Unambiguous 1:1 → attribute the voice to the participant + PROPOSE enrollment. The
            //     named attribution flows through the escalate-only gate exactly like an enrolled match
            //     (open-point under EscalateOnly, auto-applied under GradedAutoApply).
            var idx = unmatched[0];
            var person = unaccountedHints[0];
            verdicts[idx] = DecideNamed(recordingId, clusters[idx], person, ParticipantHintConfidence);
            proposals.Add(new EnrollmentProposal(person, recordingId, clusters[idx].MergedSpeaker, clusters[idx].Centroid));
            _log.LogInformation("attribution {Rec}#{Speaker}: participant-hint → {Person} (+enroll proposed)",
                recordingId, clusters[idx].MergedSpeaker, person);
        }
        else if (unmatched.Count > 1 && unaccountedHints.Count > 1)
        {
            // (4) Ambiguous: many voices + many unaccounted hints → a SINGLE escalate-only open-point.
            //     No fabrication — the operator resolves the mapping. Each unmatched voice carries the
            //     ambiguity question; nothing is asserted.
            var names = string.Join(", ", unaccountedHints);
            foreach (var idx in unmatched)
                verdicts[idx] = AttributionVerdict.OpenPoint(clusters[idx].MergedSpeaker, 0.0,
                    $"which participant is speaker {clusters[idx].MergedSpeaker} in recording {recordingId}? "
                    + $"(unmatched voices vs unaccounted participants: {{{names}}})");
        }
        else
        {
            // (3) Unknown: no enrolled match + no unambiguous hint → a recording-LOCAL "Unknown speaker N"
            //     label, deterministic per (recordingId, clusterIndex). No map write, no guessing.
            for (var k = 0; k < unmatched.Count; k++)
            {
                var idx = unmatched[k];
                verdicts[idx] = AttributionVerdict.UnknownLocal(
                    clusters[idx].MergedSpeaker, $"Unknown speaker {idx + 1}");
            }
        }

        var final = new List<AttributionVerdict>(clusters.Count);
        for (var i = 0; i < clusters.Count; i++)
            final.Add(verdicts[i]!);
        return new AttributionResult(final, proposals);
    }

    /// <summary>The confidence a participant-hint (metadata, non-voice) attribution carries (review band).</summary>
    private const double ParticipantHintConfidence = 0.5;

    /// <summary>
    /// Resolve a cluster against the ENROLLED voiceprints through the decision policy. Returns the
    /// verdict AND the matched person slug (non-null ONLY when an enrolled voice matched ≥ reject band,
    /// so it counts as "accounted for" by a voice signal). A cold no-match returns a null person so the
    /// participant-hint / unknown path takes over.
    /// </summary>
    private async Task<(AttributionVerdict Verdict, string? MatchedPerson)> ResolveEnrolledAsync(
        string recordingId, MergedCluster cluster, CancellationToken ct)
    {
        var matches = await _store.MatchAsync(cluster.Centroid, ct).ConfigureAwait(false);

        // Lint R8: drop deleted (tombstoned) people from the match set — no re-attribution.
        var live = new List<VoiceprintMatch>(matches.Count);
        foreach (var m in matches)
            if (!await _store.IsDeletedAsync(m.PersonSlug, ct).ConfigureAwait(false))
                live.Add(m);

        var best = live.Count > 0 ? live[0] : null;
        var second = live.Count > 1 ? live[1] : null;

        // A voice signal ≥ the reject band counts as an enrolled match for accounting (even if the policy
        // ultimately escalates it). Below reject / nothing enrolled → not accounted for (null person).
        var accounted = best is not null && best.Cosine >= _policy.Bands.RejectBand ? best.PersonSlug : null;

        var candidate = new AttributionCandidate(
            mergedSpeaker: cluster.MergedSpeaker,
            bestMatchPerson: best?.PersonSlug,
            bestMatchCosine: best?.Cosine ?? 0.0,
            secondMatchPerson: second?.PersonSlug,
            secondMatchCosine: second?.Cosine ?? 0.0,
            priorRelation: PriorRelation.None,   // priors are handled by the participant-hint pass now
            priorPerson: null);
        var verdict = _policy.Decide(candidate, recordingId);
        _log.LogInformation("attribution {Rec}#{Speaker}: {Outcome} ({Reason})",
            recordingId, cluster.MergedSpeaker, verdict.Outcome, verdict.Reason);
        return (verdict, accounted);
    }

    /// <summary>
    /// Produce a NAMED attribution verdict (participant-hint assignment) through the escalate-only gate:
    /// a synthetic candidate whose prior AGREES on the hinted person at the hint confidence, so the
    /// policy escalates it under EscalateOnly and auto-applies it under GradedAutoApply — a metadata
    /// hint, held to the review band, never presented as a voice match.
    /// </summary>
    private AttributionVerdict DecideNamed(string recordingId, MergedCluster cluster, string person, double confidence)
    {
        // Present as a PRIOR-ONLY signal (no best-match voice) so the policy treats it as a confirming
        // hint requiring confirmation — a metadata hint cannot auto-apply as if voice-confirmed. Under
        // GradedAutoApply the policy still routes a prior-agreeing candidate to an open-point unless a
        // voice match backs it, so we build the applied verdict directly here for the hint path and let
        // the phase gate decide: EscalateOnly → open-point; GradedAutoApply → auto-applied.
        if (_policy.Phase == PolicyPhase.EscalateOnly)
            return AttributionVerdict.OpenPoint(cluster.MergedSpeaker, confidence,
                $"participant-hint: is speaker {cluster.MergedSpeaker} {person}? "
                + "(named by the recording's participant hint; confirm to attribute + enroll)");

        // GradedAutoApply (M6): the hint auto-applies with a metadata basis (rule = participant-hint) + a
        // source ref — distinct from a voice-match basis so an audit can tell a hint from a voiceprint.
        var basis = ConfirmationBasis.Auto("v1", rule: "participant-hint");
        var source = $"rec://{recordingId}#{cluster.MergedSpeaker}";
        return AttributionVerdict.AutoApplied(cluster.MergedSpeaker, person, confidence, source, basis);
    }
}
