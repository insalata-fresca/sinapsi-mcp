using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Math;
using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// In-memory <see cref="IVoiceprintStore"/> for tests + the offline slice (spec
/// <c>voiceprint-store</c>). Mirrors the CT146 pgvector <c>PgVoiceprintStore</c> contract without
/// a database: enroll/refine as a RUNNING CENTROID, cosine match, and the OPERATIONS §7 deletion
/// runbook (delete centroid + enrollment rows + audio segments, tombstone the person so lint R8
/// "no re-attribution after deletion" holds).
///
/// <para>Confinement (spec + DESIGN §10.4): centroids live only in this store — the analogue of
/// "only in CT146 pgvector". A synthetic vector is all that ever enters it in tests; no personal
/// audio, no biometric vector is committed to git. Enrollment is hard-gated by the §10 allowlist.</para>
/// </summary>
public sealed class InMemoryVoiceprintStore(EnrollmentAllowlist allowlist) : IVoiceprintStore
{
    private readonly EnrollmentAllowlist _allowlist =
        allowlist ?? throw new ArgumentNullException(nameof(allowlist));

    private readonly Dictionary<string, Voiceprint> _prints = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deleted = new(StringComparer.Ordinal);

    /// <summary>The enrollment-audio segment blobs, keyed by person — purged by the deletion runbook.</summary>
    private readonly Dictionary<string, List<string>> _enrollmentAudio = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<VoiceprintMatch>> MatchAsync(
        IReadOnlyList<float> centroid, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(centroid);
        var matches = _prints.Values
            .Select(vp => new VoiceprintMatch(vp.PersonSlug, Cosine.Similarity(centroid, vp.Centroid)))
            .OrderByDescending(m => m.Cosine)
            .ThenBy(m => m.PersonSlug, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult<IReadOnlyList<VoiceprintMatch>>(matches);
    }

    public Task<Voiceprint?> GetAsync(string personSlug, CancellationToken ct = default) =>
        Task.FromResult(_prints.GetValueOrDefault(personSlug));

    public Task<Voiceprint> EnrollOrRefineAsync(
        string personSlug,
        IReadOnlyList<float> confirmedCentroid,
        IReadOnlyList<string> sourceSegments,
        double? matchCosine,
        DateOnly on,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(personSlug))
            throw new ArgumentException("personSlug must be non-empty", nameof(personSlug));
        ArgumentNullException.ThrowIfNull(confirmedCentroid);
        ArgumentNullException.ThrowIfNull(sourceSegments);

        // §10 allowlist — hard gate. A refused enrollment throws (never a silent skip).
        if (!_allowlist.IsAllowed(personSlug))
            throw new EnrollmentNotAllowedException(personSlug);

        // A deletion tombstone is cleared by a fresh operator-confirmed enrollment (re-consent):
        // the person is being re-enrolled deliberately, so lint R8 no longer blocks attributions.
        _deleted.Remove(personSlug);

        var audio = _enrollmentAudio.TryGetValue(personSlug, out var seg) ? seg : _enrollmentAudio[personSlug] = [];
        audio.AddRange(sourceSegments);

        Voiceprint updated;
        if (_prints.TryGetValue(personSlug, out var existing))
        {
            // Refine: running centroid weighted by prior sample_count (so old samples aren't lost),
            // sample_count++ (spec: "Subsequent confirmation refines … increments to 2").
            var newCount = existing.SampleCount + 1;
            var refined = WeightedCentroid(existing.Centroid, existing.SampleCount, confirmedCentroid, 1);
            var segments = existing.SourceSegments.Concat(sourceSegments).ToList();
            updated = new Voiceprint(personSlug, refined, newCount, existing.EnrolledAt, matchCosine, segments);
        }
        else
        {
            if (confirmedCentroid.Count != SpeakerEmbedding.ExpectedDim)
                throw new ArgumentException(
                    $"confirmedCentroid must be {SpeakerEmbedding.ExpectedDim}-d", nameof(confirmedCentroid));
            updated = new Voiceprint(personSlug, confirmedCentroid, sampleCount: 1, on, matchCosine, sourceSegments);
        }

        _prints[personSlug] = updated;
        return Task.FromResult(updated);
    }

    public Task<bool> DeleteAsync(string personSlug, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(personSlug))
            throw new ArgumentException("personSlug must be non-empty", nameof(personSlug));

        // OPERATIONS §7 step 1+2: delete centroid + enrollment rows, purge enrollment audio.
        var existed = _prints.Remove(personSlug);
        _enrollmentAudio.Remove(personSlug);

        // OPERATIONS §7 step 4: tombstone so a restore-from-backup re-runs the runbook and lint R8
        // blocks any new voice-match attribution (steps 3+5 — re-mark historical, log — are the
        // graph-writer's + access-log's responsibility, driven by the caller).
        _deleted.Add(personSlug);
        return Task.FromResult(existed);
    }

    public Task<bool> IsDeletedAsync(string personSlug, CancellationToken ct = default) =>
        Task.FromResult(!string.IsNullOrWhiteSpace(personSlug) && _deleted.Contains(personSlug));

    /// <summary>Whether the person's enrollment audio blob dir is empty (deletion-runbook evidence).</summary>
    public bool HasEnrollmentAudio(string personSlug) =>
        _enrollmentAudio.TryGetValue(personSlug, out var seg) && seg.Count > 0;

    /// <summary>
    /// Sample-weighted element-wise mean of two centroids: <c>(a*na + b*nb) / (na+nb)</c>. Keeps
    /// the running centroid faithful to the true mean over all confirmed samples.
    /// </summary>
    private static float[] WeightedCentroid(
        IReadOnlyList<float> a, int na, IReadOnlyList<float> b, int nb)
    {
        if (a.Count != b.Count)
            throw new ArgumentException("centroid dimension mismatch on refine");
        var dim = a.Count;
        var total = na + nb;
        var result = new float[dim];
        for (var i = 0; i < dim; i++)
            result[i] = (float)((a[i] * (double)na + b[i] * (double)nb) / total);
        return result;
    }
}
