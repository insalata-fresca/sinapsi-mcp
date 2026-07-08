using System.Collections.Concurrent;
using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// Live <see cref="IEnrollmentSourceProvider"/> backed by the recording's DIARIZED cluster centroids
/// (spec <c>open-points-mcp</c> → "Answering a speaker point applies and enrolls"). The redacted
/// <c>OpenPoint</c> carries NO vector (R10 / biometric confinement), so when the operator answers a
/// speaker point the answer path fetches the confirmed merged-cluster centroid from HERE — keyed by
/// (recording, mergedSpeaker) — to feed <c>VoiceprintEnrollment</c>.
///
/// <para>The centroids are the TRANSIENT diarize-embed output for the recording, held CT-side only
/// for the lifetime of the open-point (never git, never a shared subject). The
/// <c>AttributionStage</c> populates this provider with the merged centroids as it processes a
/// recording; the answer path reads them. An unknown (recording, speaker) → null (enroll is skipped;
/// the attribution is still applied, but nothing is written to the voiceprint store).</para>
///
/// <para>This is the SAME contract the <c>FakeEnrollmentSourceProvider</c> in tests satisfies; the
/// only difference is that the live source is fed from the pipeline's transient diarize output
/// rather than pre-seeded. The (recording, speaker)→centroid population is wired at the pipeline
/// seam; L2 verifies the end-to-end enroll-on-answer with a real diarized recording.</para>
/// </summary>
public sealed class DiarizedCentroidEnrollmentSourceProvider : IEnrollmentSourceProvider
{
    private readonly ConcurrentDictionary<string, EnrollmentSource> _sources = new(StringComparer.Ordinal);

    /// <summary>
    /// Register a merged cluster's confirmed centroid + segment refs for (recording, speaker). Called
    /// by the attribution stage as it diarizes a recording (transient, CT-side). Idempotent overwrite.
    /// </summary>
    public void Register(string recordingId, string mergedSpeaker, EnrollmentSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _sources[Key(recordingId, mergedSpeaker)] = source;
    }

    /// <summary>Drop a recording's transient centroids once its open-points are all resolved (custody).</summary>
    public void Evict(string recordingId, string mergedSpeaker) =>
        _sources.TryRemove(Key(recordingId, mergedSpeaker), out _);

    public Task<EnrollmentSource?> GetConfirmedSourceAsync(string recordingId, string mergedSpeaker, CancellationToken ct = default)
    {
        _sources.TryGetValue(Key(recordingId, mergedSpeaker), out var s);
        return Task.FromResult(s);
    }

    private static string Key(string rec, string spk) => $"{rec}#{spk}";
}
