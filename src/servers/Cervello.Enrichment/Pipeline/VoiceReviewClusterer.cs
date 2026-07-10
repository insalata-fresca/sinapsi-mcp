using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Math;
using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Pipeline;

/// <summary>
/// One-shot, READ-ONLY review clustering of the voiceprint corpus into distinct candidate voices
/// (design doc <c>ste/cervello</c> <c>docs/design/voiceprint-naming.md</c> §7 phase V1, §3). Groups
/// every persisted <see cref="RecordingVoiceprint"/> centroid (<see
/// cref="IRecordingVoiceprintStore.GetCorpusAsync"/>) across the WHOLE corpus by cosine similarity,
/// reusing <see cref="Cosine"/> and the same single-linkage union-find approach as
/// <see cref="ClusterMerge"/> — but at a DIFFERENT, cross-recording cutoff (see
/// <see cref="DefaultReviewCutoff"/>), and producing a fresh SNAPSHOT every call rather than a
/// persisted identity.
///
/// <para><b>Deliberately NOT the descoped M4 resolver.</b> <c>docs/design/autonomous-attribution.md</c>
/// §0 records that <c>CorpusSpeakerResolver</c> / <c>CorpusClustering</c> / the <c>speaker_clusters</c>
/// registry were REMOVED — a persistent, cross-recording, online-clustering-with-identity system
/// judged research-grade and unstable (silent orphaning, centroid drift, non-deterministic
/// re-clustering). This type sidesteps every one of those failure modes by construction: it
/// PERSISTS NOTHING, it NEVER mutates on a new recording, and it is recomputed fresh — read-only
/// compute over the existing corpus, thrown away after the caller acts on it (typically: cut a
/// representative clip per voice and hand it to the operator to name).</para>
///
/// <para><b>Determinism.</b> For a fixed corpus snapshot + cutoff, clustering is deterministic:
/// members are read in the store's stable <c>(RecordingId, ClusterIndex)</c> order, single-linkage
/// union-find is a pure function of the pairwise cosines, and output voices are ordered by
/// descending total duration (ties broken by the first-contributing recording id) — the same input
/// always yields the same grouping AND the same <see cref="VoiceReviewCluster.LocalId"/> assignment.</para>
///
/// <para><b>Confinement.</b> Every centroid this reads/returns is CT146-only biometric material
/// (design §10.4) — this type never writes it anywhere, never crosses a NATS subject, never touches
/// Drive/git. It is pure in-process compute over what <see cref="IRecordingVoiceprintStore"/> already
/// holds.</para>
/// </summary>
public sealed class VoiceReviewClusterer
{
    /// <summary>
    /// The cross-recording review cutoff. A DIFFERENT knob from <see cref="ClusterMerge.DefaultMergeCutoff"/>
    /// (0.62, within-recording): cross-recording same-speaker cosine runs lower and noisier
    /// (<c>autonomous-attribution.md</c> §3.e: "distinct enough not to merge two people, cosine cut
    /// 0.55"). Left CONFIGURABLE (<c>CERVELLO_VOICE_REVIEW_CUT</c>) — to be fit on the operator's real
    /// corpus at P5, not assumed; this default is a starting point, not a validated value.
    /// </summary>
    public const double DefaultReviewCutoff = 0.58;

    private readonly IRecordingVoiceprintStore _corpusStore;
    private readonly IVoiceprintStore? _enrolledStore;

    /// <param name="corpusStore">The M3 per-recording centroid corpus this clusters over.</param>
    /// <param name="enrolledStore">
    /// OPTIONAL §10-enrolled voiceprint store. When supplied, a review cluster whose representative
    /// centroid cosine-matches an already-enrolled person above <paramref name="alreadyEnrolledCutoff"/>
    /// in <see cref="ClusterAsync"/> is excluded from the unnamed candidates (design §3: "Already-enrolled
    /// voices are matched-and-excluded … so the operator only names the unknowns"). Null = no
    /// exclusion (every voice returned as a candidate) — used offline / when nobody is enrolled yet.
    /// </param>
    public VoiceReviewClusterer(IRecordingVoiceprintStore corpusStore, IVoiceprintStore? enrolledStore = null)
    {
        ArgumentNullException.ThrowIfNull(corpusStore);
        _corpusStore = corpusStore;
        _enrolledStore = enrolledStore;
    }

    /// <summary>
    /// Run the one-shot review clustering: fetch the whole corpus, group by cosine at
    /// <paramref name="reviewCutoff"/>, exclude any voice matching an already-enrolled person above
    /// <paramref name="alreadyEnrolledCutoff"/> (only if an enrolled store was supplied), rank the
    /// rest by coverage (<see cref="VoiceReviewCluster.TotalDurationSeconds"/> descending), and cap
    /// at <paramref name="maxCandidates"/> — "the top ~15 are the enrollment candidates" (design §3).
    /// Nothing here is persisted; call again any time to get a fresh snapshot.
    /// </summary>
    public async Task<IReadOnlyList<VoiceReviewCluster>> ClusterAsync(
        double reviewCutoff = DefaultReviewCutoff,
        double alreadyEnrolledCutoff = 0.62,
        int maxCandidates = 15,
        CancellationToken ct = default)
    {
        if (maxCandidates < 0)
            throw new ArgumentException("maxCandidates must be >= 0", nameof(maxCandidates));

        var corpus = await _corpusStore.GetCorpusAsync(ct).ConfigureAwait(false);
        var voices = Cluster(corpus, reviewCutoff);

        if (_enrolledStore is not null)
        {
            var unenrolled = new List<VoiceReviewCluster>(voices.Count);
            foreach (var voice in voices)
            {
                var matches = await _enrolledStore.MatchAsync(voice.RepresentativeCentroid, ct).ConfigureAwait(false);
                var alreadyEnrolled = matches.Any(m => m.Cosine >= alreadyEnrolledCutoff);
                if (!alreadyEnrolled)
                    unenrolled.Add(voice);
            }
            voices = unenrolled;
        }

        return voices.Take(maxCandidates).ToList();
    }

    /// <summary>
    /// Pure, synchronous grouping step (no I/O) — kept separate from <see cref="ClusterAsync"/> so
    /// the core algorithm is unit-testable without a store. Single-linkage union-find over corpus
    /// rows at <paramref name="reviewCutoff"/>, output ranked by descending total duration
    /// (ties broken by the first contributing recording id, ordinal) for a deterministic snapshot.
    /// </summary>
    public static IReadOnlyList<VoiceReviewCluster> Cluster(
        IReadOnlyList<RecordingVoiceprint> corpus, double reviewCutoff = DefaultReviewCutoff)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        var n = corpus.Count;
        if (n == 0) return Array.Empty<VoiceReviewCluster>();

        // Deterministic input order regardless of what the store handed us.
        var rows = corpus
            .OrderBy(r => r.RecordingId, StringComparer.Ordinal)
            .ThenBy(r => r.ClusterIndex)
            .ToList();

        // Single-linkage union-find over cosine similarity — identical shape to ClusterMerge, but a
        // DIFFERENT (cross-recording) cutoff, and grouping (recordingId, clusterIndex) rows instead
        // of SpeakerClusters.
        var parent = new int[n];
        for (var i = 0; i < n; i++) parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }

        void Union(int a, int b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb) parent[System.Math.Max(ra, rb)] = System.Math.Min(ra, rb);
        }

        for (var i = 0; i < n; i++)
        for (var j = i + 1; j < n; j++)
        {
            var sim = Cosine.Similarity(rows[i].Centroid, rows[j].Centroid);
            if (sim >= reviewCutoff) Union(i, j);
        }

        var groups = new Dictionary<int, List<int>>();
        for (var i = 0; i < n; i++)
        {
            var r = Find(i);
            if (!groups.TryGetValue(r, out var list)) groups[r] = list = [];
            list.Add(i);
        }

        // Build one VoiceReviewCluster per group (LocalId assigned after ranking, below).
        var unranked = new List<(IReadOnlyList<float> Centroid, IReadOnlyList<VoiceReviewMember> Members, string FirstRecordingId)>();
        foreach (var group in groups.Values)
        {
            var members = group.Select(i => rows[i]).ToList();
            var centroid = Cosine.Centroid(members.Select(m => m.Centroid).ToList());
            var reviewMembers = members
                .Select(m => new VoiceReviewMember(m.RecordingId, m.ClusterIndex, m.DurationSeconds, m.SegmentCount))
                .ToList();
            var firstRecordingId = members.Select(m => m.RecordingId).OrderBy(id => id, StringComparer.Ordinal).First();
            unranked.Add((centroid, reviewMembers, firstRecordingId));
        }

        // Rank by coverage (total duration) descending — "so the top ~15 are the enrollment
        // candidates" (design §3). Tie-break on first contributing recording id for determinism.
        var ranked = unranked
            .OrderByDescending(v => v.Members.Sum(m => m.DurationSeconds))
            .ThenBy(v => v.FirstRecordingId, StringComparer.Ordinal)
            .ToList();

        var result = new List<VoiceReviewCluster>(ranked.Count);
        for (var i = 0; i < ranked.Count; i++)
        {
            var (centroid, members, _) = ranked[i];
            result.Add(new VoiceReviewCluster($"voice_{i:D2}", centroid, members));
        }
        return result;
    }
}
