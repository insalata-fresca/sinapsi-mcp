namespace Cervello.Watcher.Domain;

/// <summary>
/// One <c>recordings/manifest.yaml</c> entry, the git-side §8 record. Field order
/// is fixed (SCHEMAS §8): id, audio_sha256, source_drive_id, transcript,
/// google_txt, attribution, recorded_at, state. Carries references + checksums
/// ONLY — never audio bytes (invariant / custody).
/// </summary>
public sealed record ManifestEntry
{
    public ManifestEntry(
        string id,
        string audioSha256,
        string sourceDriveId,
        string transcript,
        string? googleTxt,
        string attribution,
        string recordedAt,
        string state)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("ManifestEntry.Id must be non-empty", nameof(id));
        if (string.IsNullOrWhiteSpace(audioSha256))
            throw new ArgumentException("ManifestEntry.AudioSha256 must be non-empty", nameof(audioSha256));
        if (string.IsNullOrWhiteSpace(sourceDriveId))
            throw new ArgumentException("ManifestEntry.SourceDriveId must be non-empty", nameof(sourceDriveId));
        Id = id;
        AudioSha256 = audioSha256;
        SourceDriveId = sourceDriveId;
        Transcript = transcript;
        GoogleTxt = googleTxt;
        Attribution = attribution;
        RecordedAt = recordedAt;
        State = state;
    }

    public string Id { get; }
    public string AudioSha256 { get; }
    public string SourceDriveId { get; }

    /// <summary>Planned ENRICH output path, not created now (D6): <c>recordings/transcripts/&lt;id&gt;.md</c>.</summary>
    public string Transcript { get; }

    /// <summary>The Drive fileId of the raw Google <c>.txt</c> (content hint, §8).</summary>
    public string? GoogleTxt { get; }

    /// <summary>Always <c>pending</c> at NORMALIZE (D6).</summary>
    public string Attribution { get; }
    public string RecordedAt { get; }

    /// <summary>Always <c>normalized</c> when written by this stage.</summary>
    public string State { get; }

    /// <summary>Build the canonical §8 entry for a paired recording (D6 field values).</summary>
    public static ManifestEntry ForRecording(Recording r) => new(
        id: r.Id,
        audioSha256: r.AudioSha256,
        sourceDriveId: r.AudioDriveId,
        transcript: $"recordings/transcripts/{r.Id}.md",
        googleTxt: r.TxtDriveId,
        attribution: "pending",
        recordedAt: r.RecordedAt,
        state: "normalized");
}
