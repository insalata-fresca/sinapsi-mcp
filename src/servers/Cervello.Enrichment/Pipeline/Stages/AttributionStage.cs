using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Policy;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cervello.Enrichment.Pipeline.Stages;

/// <summary>
/// The resolve(Speaker N → person) stage (spec <c>speaker-attribution</c>; DESIGN §5.2 step 2).
/// For each merged speaker cluster it: computes the org-chart prior (<see cref="IPriorSource"/>),
/// cosine-matches the cluster centroid against enrolled voiceprints (<see cref="IVoiceprintStore"/>),
/// assembles an <see cref="AttributionCandidate"/>, and routes it through the
/// <see cref="DecisionPolicy"/> — auto / flag / escalate / omit, never a guess.
///
/// <para>Lint R8 — "no re-attribution after deletion": a person whose voiceprint was DELETED is
/// dropped from the match set before the policy runs, so no voice-match attribution to them can
/// be produced.</para>
///
/// <para>Assistive-only: this stage produces VERDICTS. Enroll/refine is NOT done here — it is
/// triggered downstream by a CONFIRMATION (operator answer or an uncorrected P5 auto-apply), per
/// <see cref="VoiceprintEnrollment"/>. The stage never enrolls from its own unconfirmed match.</para>
/// </summary>
public sealed class AttributionStage(
    IVoiceprintStore store,
    IPriorSource priorSource,
    DecisionPolicy policy,
    ILogger<AttributionStage>? logger = null)
{
    private readonly IVoiceprintStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IPriorSource _priorSource = priorSource ?? throw new ArgumentNullException(nameof(priorSource));
    private readonly DecisionPolicy _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    private readonly ILogger _log = logger ?? NullLogger<AttributionStage>.Instance;

    /// <summary>Resolve every merged cluster for a recording into a decision-policy verdict.</summary>
    public async Task<IReadOnlyList<AttributionVerdict>> ResolveAsync(
        string recordingId,
        IReadOnlyList<MergedCluster> clusters,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(recordingId))
            throw new ArgumentException("recordingId must be non-empty", nameof(recordingId));
        ArgumentNullException.ThrowIfNull(clusters);

        var prior = await _priorSource.GetPriorAsync(recordingId, ct).ConfigureAwait(false);

        var verdicts = new List<AttributionVerdict>(clusters.Count);
        foreach (var cluster in clusters)
        {
            var candidate = await BuildCandidateAsync(cluster, prior, ct).ConfigureAwait(false);
            var verdict = _policy.Decide(candidate, recordingId);
            _log.LogInformation("attribution {Rec}#{Speaker}: {Outcome} ({Reason})",
                recordingId, cluster.MergedSpeaker, verdict.Outcome, verdict.Reason);
            verdicts.Add(verdict);
        }

        return verdicts;
    }

    private async Task<AttributionCandidate> BuildCandidateAsync(
        MergedCluster cluster, PriorCandidates prior, CancellationToken ct)
    {
        var matches = await _store.MatchAsync(cluster.Centroid, ct).ConfigureAwait(false);

        // Lint R8: drop deleted (tombstoned) people from the match set — no re-attribution.
        var live = new List<VoiceprintMatch>(matches.Count);
        foreach (var m in matches)
            if (!await _store.IsDeletedAsync(m.PersonSlug, ct).ConfigureAwait(false))
                live.Add(m);

        var best = live.Count > 0 ? live[0] : null;
        var second = live.Count > 1 ? live[1] : null;

        var relation = ResolvePriorRelation(prior, best?.PersonSlug);
        var priorPerson = prior.CandidatePersonSlugs.Count > 0 ? prior.CandidatePersonSlugs[0] : null;

        return new AttributionCandidate(
            mergedSpeaker: cluster.MergedSpeaker,
            bestMatchPerson: best?.PersonSlug,
            bestMatchCosine: best?.Cosine ?? 0.0,
            secondMatchPerson: second?.PersonSlug,
            secondMatchCosine: second?.Cosine ?? 0.0,
            priorRelation: relation,
            priorPerson: priorPerson);
    }

    /// <summary>
    /// How the prior relates to the best voice match: AGREES if the prior includes the matched
    /// person; CONFLICTS if the prior is STRONG and points at someone else; NONE otherwise (weak
    /// or empty prior neither confirms nor conflicts).
    /// </summary>
    private static PriorRelation ResolvePriorRelation(PriorCandidates prior, string? bestMatchPerson)
    {
        if (prior.IsEmpty) return PriorRelation.None;
        if (bestMatchPerson is not null && prior.Includes(bestMatchPerson)) return PriorRelation.Agrees;
        // A strong prior that names people, none of which is the voice match → conflict.
        return prior.IsStrong ? PriorRelation.Conflicts : PriorRelation.None;
    }
}
