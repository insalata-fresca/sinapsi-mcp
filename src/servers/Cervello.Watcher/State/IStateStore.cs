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
}
