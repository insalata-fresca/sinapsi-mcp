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
        // audio_sha256 is EMPTY for a transcript-only recording (no audio); the source_drive_id then
        // points at the transcript's Drive fileId so the §8 entry still references a real Drive source.
        // A recording with NEITHER an audio sha NOR a source drive id is not a valid §8 record.
        if (string.IsNullOrWhiteSpace(sourceDriveId))
            throw new ArgumentException("ManifestEntry.SourceDriveId must be non-empty", nameof(sourceDriveId));
        Id = id;
        AudioSha256 = audioSha256 ?? "";
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

    /// <summary>
    /// Build the canonical §8 entry for a recording (D6 field values). For a transcript-only
    /// recording (no audio) <c>audio_sha256</c> is empty and <c>source_drive_id</c> falls back to the
    /// transcript's Drive fileId — the entry still references a real Drive source. <c>google_txt</c>
    /// carries the transcript Drive id whenever a transcript is present (both + transcript-only).
    /// </summary>
    public static ManifestEntry ForRecording(Recording r) => new(
        id: r.Id,
        audioSha256: r.AudioSha256,                             // "" for transcript-only
        sourceDriveId: r.HasAudio ? r.AudioDriveId : r.TxtDriveId ?? "",
        transcript: $"recordings/transcripts/{r.Id}.md",
        googleTxt: r.TxtDriveId,
        attribution: "pending",
        recordedAt: r.RecordedAt,
        state: "normalized");
}
