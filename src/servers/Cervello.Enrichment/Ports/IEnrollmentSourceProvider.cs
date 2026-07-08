namespace Cervello.Enrichment.Ports;

/// <summary>
/// Supplies the CONFIRMED enrollment source (centroid + segment refs) for a speaker open-point when
/// the operator answers it (spec <c>open-points-mcp</c> → "Answering a speaker point applies and
/// enrolls"). The redacted <c>OpenPoint</c> deliberately carries NO vector (R10 / biometric
/// confinement), so the answer path fetches the confirmed cluster's centroid from this provider —
/// keyed by (recording, mergedSpeaker) — to feed <c>VoiceprintEnrollment</c>. In prod this reads
/// the recording's diarized cluster centroids (CT146, transient); in tests it is a fake.
/// </summary>
public interface IEnrollmentSourceProvider
{
    /// <summary>
    /// The confirmed centroid + source segment refs for a merged speaker cluster, or null if the
    /// cluster is unknown (in which case enroll is skipped — the attribution is still applied,
    /// but nothing is written to the voiceprint store).
    /// </summary>
    Task<EnrollmentSource?> GetConfirmedSourceAsync(string recordingId, string mergedSpeaker, CancellationToken ct = default);
}

/// <summary>The confirmed centroid + segment refs used to enroll/refine a voiceprint on answer.</summary>
public sealed record EnrollmentSource(IReadOnlyList<float> Centroid, IReadOnlyList<string> SourceSegments, double? MatchCosine);
