namespace Cervello.Enrichment.Ports;

/// <summary>
/// Port for persisting the base transcript at <c>recordings/transcripts/&lt;id&gt;.md</c>
/// (spec <c>text-correction</c> → "Base transcript persisted before correction"). Abstracted
/// so <c>BaseTranscribeStage</c> stays testable without touching the repo working tree; the
/// live adapter (E4) writes the git-side markdown. The base is written once and never
/// overwritten by the correction stage.
/// </summary>
public interface ITranscriptStore
{
    /// <summary>The repo-relative path a recording's transcript lives at (SCHEMAS §8).</summary>
    string TranscriptPath(string recordingId);

    /// <summary>True if a base transcript already exists for this recording (idempotency).</summary>
    Task<bool> ExistsAsync(string recordingId, CancellationToken ct = default);

    /// <summary>Persist the base transcript. Returns the repo-relative path written.</summary>
    Task<string> WriteBaseAsync(string recordingId, BaseTranscript transcript, CancellationToken ct = default);

    /// <summary>
    /// The repo-relative path a recording's corrected + speaker-labeled document lives at (SCHEMAS §8
    /// manifest <c>attribution:</c> field: <c>recordings/attributions/&lt;id&gt;.md</c>). M5 — distinct
    /// from the immutable <see cref="TranscriptPath"/> base; this artifact carries the corrected text
    /// (evidence-gated diffs applied) plus the M4 speaker roster.
    /// </summary>
    string AttributionPath(string recordingId);

    /// <summary>
    /// Persist (create-or-update) the corrected + speaker-labeled markdown at
    /// <see cref="AttributionPath"/>. Unlike <see cref="WriteBaseAsync"/> this is NOT write-once — a
    /// later run (fresh correction/attribution) supersedes the prior content, mirroring the base
    /// transcript's own re-publish-in-place contract. Returns the repo-relative path written.
    /// </summary>
    Task<string> WriteAttributionAsync(string recordingId, string markdown, CancellationToken ct = default);
}
