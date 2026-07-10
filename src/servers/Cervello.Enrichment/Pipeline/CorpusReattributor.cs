using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Math;
using Cervello.Enrichment.Policy;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cervello.Enrichment.Pipeline;

/// <summary>
/// V6 — corpus re-attribution / propagation (design <c>ste/cervello</c>
/// <c>docs/design/voiceprint-naming.md</c> §7 phase V6, §1.6/§6.6–6.7). After V5 enrolls a person from
/// an operator rename, this finds the recordings in the <see cref="IRecordingVoiceprintStore"/> corpus
/// whose per-cluster centroids match the newly-enrolled print, and RESETS ONLY those to
/// <c>normalized</c> so the existing <see cref="Stages.DrainWorker"/> drain re-runs
/// <see cref="Stages.AttributionStage"/> — now the print is enrolled, so the match lands and the corpus
/// self-labels.
///
/// <para><b>Bands (the write-safety heart — §9 forks 2 &amp; 3).</b> Each corpus row's cosine to the new
/// print is bucketed by the SAME <see cref="DecisionBands"/> the attribution policy uses:</para>
/// <list type="bullet">
///   <item><b>≥ auto band</b> → the recording is requeued AND the just-enrolled slug is marked in
///     <see cref="IRecentEnrollmentStore"/>, so when the drain re-runs, the AttributionStage
///     AUTO-APPLIES the label carrying the enrollment's <c>human://</c> basis (decision #2:
///     "match to a print the operator enrolled this session auto-applies") — even under the global
///     escalate-only phase. The corpus labels itself; no 15 open-points.</item>
///   <item><b>reject ≤ cosine &lt; auto (borderline)</b> → the recording is requeued so the drain
///     re-runs, but the recent-enrollment mark does NOT authorise auto-apply for a below-auto cosine,
///     so the AttributionStage ESCALATES it to an open-point (decision #3: "borderline matches still
///     ESCALATE, not mislabel"). The requeue re-surfaces the question; it never asserts the name.</item>
///   <item><b>&lt; reject band</b> → NOT a match → the recording is NEVER touched (design §6.6 "Only
///     reset recordings that actually match — don't blanket-reprocess").</item>
/// </list>
///
/// <para>Enrolls/attributes ONLY the person the operator just named, against ONLY the recordings that
/// actually match the exact enrolled centroid — never a different voice, never a blanket reprocess.
/// The dossier + per-recording attribution writes ride the existing map-PR path when the drain re-runs
/// (§6.7) — this component owns only the SELECT + requeue + the auto-apply authorisation signal.</para>
/// </summary>
public sealed class CorpusReattributor(
    IRecordingVoiceprintStore corpusStore,
    IRecordingRequeue requeue,
    IRecentEnrollmentStore recentEnrollment,
    DecisionBands? bands = null,
    ILogger<CorpusReattributor>? logger = null)
{
    private readonly IRecordingVoiceprintStore _corpus =
        corpusStore ?? throw new ArgumentNullException(nameof(corpusStore));
    private readonly IRecordingRequeue _requeue = requeue ?? throw new ArgumentNullException(nameof(requeue));
    private readonly IRecentEnrollmentStore _recent =
        recentEnrollment ?? throw new ArgumentNullException(nameof(recentEnrollment));
    private readonly DecisionBands _bands = bands ?? DecisionBands.Default;
    private readonly ILogger _log = logger ?? NullLogger<CorpusReattributor>.Instance;

    /// <summary>
    /// Re-attribute the corpus for a JUST-ENROLLED person. <paramref name="enrolledCentroid"/> is the
    /// exact centroid V5 enrolled under <paramref name="personSlug"/>; <paramref name="humanBasisId"/>
    /// is the enrollment's <c>human://rename:&lt;fileId&gt;</c> basis (carried to the auto-apply).
    /// </summary>
    public async Task<ReattributionResult> ReattributeAsync(
        string personSlug,
        IReadOnlyList<float> enrolledCentroid,
        string humanBasisId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(personSlug))
            throw new ArgumentException("personSlug must be non-empty", nameof(personSlug));
        ArgumentNullException.ThrowIfNull(enrolledCentroid);
        if (string.IsNullOrWhiteSpace(humanBasisId))
            throw new ArgumentException("humanBasisId must be non-empty", nameof(humanBasisId));

        var corpus = await _corpus.GetCorpusAsync(ct).ConfigureAwait(false);

        // Bucket each recording by its BEST cosine to the new print (a recording can have several
        // clusters; we take the strongest one — that is the one that decides re-attribution).
        var bestPerRecording = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var row in corpus)
        {
            if (row.Centroid.Count != enrolledCentroid.Count)
                continue; // a different embedding space — never cross-compare (would be a garbage cosine)
            var cos = Cosine.Similarity(enrolledCentroid, row.Centroid);
            if (!bestPerRecording.TryGetValue(row.RecordingId, out var prior) || cos > prior)
                bestPerRecording[row.RecordingId] = cos;
        }

        var autoMatches = new List<string>();
        var borderlineMatches = new List<string>();
        foreach (var (recordingId, cosine) in bestPerRecording.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (_bands.IsAuto(cosine))
                autoMatches.Add(recordingId);
            else if (!_bands.IsReject(cosine)) // reject ≤ cosine < auto
                borderlineMatches.Add(recordingId);
            // else: below reject → not a match → never touched
        }

        // Authorise the SCOPED auto-apply for THIS person only when there is at least one auto-band
        // match to re-attribute. Marked BEFORE the requeue so the drain (which may run immediately) sees
        // it. Borderline-only re-attributions never mark it — they must escalate, not auto-apply.
        if (autoMatches.Count > 0)
            await _recent.MarkAsync(personSlug, humanBasisId, ct).ConfigureAwait(false);

        var requeued = new List<string>();
        var missing = new List<string>();
        foreach (var recordingId in autoMatches.Concat(borderlineMatches))
        {
            var ok = await _requeue.RequeueForReattributionAsync(recordingId, ct).ConfigureAwait(false);
            if (ok) requeued.Add(recordingId);
            else missing.Add(recordingId);
        }

        _log.LogInformation(
            "re-attribution {Person}: {Auto} auto-band + {Borderline} borderline matched; {Requeued} requeued, {Missing} missing",
            personSlug, autoMatches.Count, borderlineMatches.Count, requeued.Count, missing.Count);

        return new ReattributionResult(personSlug, autoMatches, borderlineMatches, requeued, missing);
    }
}

/// <summary>
/// The outcome of one <see cref="CorpusReattributor.ReattributeAsync"/> pass: the recordings that
/// matched the new print in the AUTO band (auto-apply the label on drain re-run) vs the BORDERLINE band
/// (re-run escalates to an open-point), and which were actually requeued vs unknown to the requeue seam.
/// </summary>
public sealed record ReattributionResult(
    string PersonSlug,
    IReadOnlyList<string> AutoBandRecordingIds,
    IReadOnlyList<string> BorderlineRecordingIds,
    IReadOnlyList<string> RequeuedRecordingIds,
    IReadOnlyList<string> MissingRecordingIds)
{
    /// <summary>Total recordings selected for re-attribution (auto + borderline).</summary>
    public int MatchedCount => AutoBandRecordingIds.Count + BorderlineRecordingIds.Count;
}
