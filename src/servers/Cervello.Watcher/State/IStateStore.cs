using Cervello.Watcher.Domain;

namespace Cervello.Watcher.State;

/// <summary>A staged download row (idempotency ledger, keyed by <c>drive:&lt;fileId&gt;:&lt;md5&gt;</c>).</summary>
public sealed record DownloadRecord(
    string DriveKey,
    string FileId,
    string? Md5,
    string Kind,
    string? StagedPath,
    string? Sha256,
    PipelineState State,
    string? Reason);

/// <summary>
/// Durable pipeline state (D4). Owns the cursor (at-least-once resumption), the
/// download idempotency ledger, and the recording ledger. InMemory (tests) and
/// Postgres (prod) implement it identically so the cursor discipline + idempotency
/// are exercised without a live DB.
/// </summary>
public interface IStateStore
{
    Task EnsureSchemaAsync(CancellationToken ct);

    // ---- cursor ----
    Task<string?> GetCursorAsync(string scope, CancellationToken ct);
    Task SetCursorAsync(string scope, string pageToken, CancellationToken ct);

    // ---- download ledger (drive:<fileId>:<md5>) ----
    Task<DownloadRecord?> GetDownloadAsync(string driveKey, CancellationToken ct);
    Task UpsertDownloadAsync(DownloadRecord record, CancellationToken ct);

    // ---- recording ledger (rec:<recordingId>:<audio_sha256>) ----
    Task<bool> RecordingExistsAsync(string recordingKey, CancellationToken ct);
    Task UpsertRecordingAsync(Recording recording, CancellationToken ct);

    /// <summary>
    /// Load the already-registered recording rows for a recording <c>id</c> (there may be more than
    /// one row per id when a single-sided family and its content-sha differ — e.g. an audio-only row
    /// keyed <c>rec:&lt;id&gt;:&lt;audioSha&gt;</c> vs a transcript-only row keyed <c>rec:&lt;id&gt;:txt:&lt;txtSha&gt;</c>).
    /// BACKFILL-SELF-HEAL uses this to detect that a recording currently exists ONLY as a single-sided
    /// row so it can UPGRADE it to a pair rather than register a second, disjoint row. Returns every
    /// row sharing the id (empty when none).
    /// </summary>
    Task<IReadOnlyList<Recording>> GetRecordingsByIdAsync(string recordingId, CancellationToken ct);

    /// <summary>
    /// Upgrade an EXISTING single-sided recording row into the full pair (or the other side): fill the
    /// previously-missing side + transcript ref and RESET its pipeline state to <c>normalized</c> so the
    /// enrichment drain reprocesses it with the correct sides. Keyed by <paramref name="oldKey"/> (the
    /// key the single-sided row was stored under, which may differ from the upgraded recording's key for
    /// a transcript-only → pair upgrade): the old row is replaced by <paramref name="upgraded"/> so no
    /// duplicate/orphan row survives. A no-op if <paramref name="oldKey"/> is absent.
    /// </summary>
    Task UpgradeRecordingAsync(string oldKey, Recording upgraded, CancellationToken ct);
}
