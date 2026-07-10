using Cervello.Enrichment.Domain;

namespace Cervello.Enrichment.Ports;

/// <summary>
/// Port for the durable <c>unknown_NN → centroid</c> candidate map V4 writes and V5's rename-poller
/// reads (design <c>ste/cervello</c> <c>docs/design/voiceprint-naming.md</c> §7 phase V4, §4.4, table
/// <c>voiceprint_naming_candidates</c>). This is the critical link between a Drive file (renamed by
/// the operator) and the voiceprint centroid to enroll — see <see cref="VoiceprintNamingCandidate"/>
/// for the full rationale.
///
/// <para>Confinement: like <see cref="IRecordingVoiceprintStore"/>, the centroid is derived biometric
/// data — CT146 pgvector ONLY, never git, never a shared subject, never off-CT.</para>
/// </summary>
public interface IVoiceprintNamingCandidateStore
{
    /// <summary>
    /// Replace the CURRENT unresolved candidate set with <paramref name="candidates"/> (design §5:
    /// "re-running regenerates the candidate set … keep it sane, don't duplicate"): every UNRESOLVED
    /// row is deleted first, then <paramref name="candidates"/> is inserted. RESOLVED rows (a rename
    /// V5 already turned into an enrollment) are NEVER touched by this call — they are the operator's
    /// durable naming decision, not a regenerable cache. Returns the Drive file ids of the deleted
    /// unresolved rows so the caller can also delete their now-orphaned Drive clips.
    /// </summary>
    Task<IReadOnlyList<string>> ReplaceUnresolvedAsync(
        IReadOnlyList<VoiceprintNamingCandidate> candidates, CancellationToken ct = default);

    /// <summary>Fetch one candidate by its Drive file id (the resolution key), or null if absent.</summary>
    Task<VoiceprintNamingCandidate?> GetByDriveFileIdAsync(string driveFileId, CancellationToken ct = default);

    /// <summary>Fetch every currently UNRESOLVED candidate, ordered by <c>SampleName</c>.</summary>
    Task<IReadOnlyList<VoiceprintNamingCandidate>> GetUnresolvedAsync(CancellationToken ct = default);

    /// <summary>
    /// Mark a candidate resolved (V5, after a successful rename→enroll). A no-op (returns false) if
    /// the Drive file id is unknown or already resolved — never throws for a replay.
    /// </summary>
    Task<bool> MarkResolvedAsync(string driveFileId, CancellationToken ct = default);
}
