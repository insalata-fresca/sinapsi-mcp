using Cervello.Watcher.Domain;
using Cervello.Watcher.State;
using Microsoft.Extensions.Logging;

namespace Cervello.Watcher.Ingest;

/// <summary>
/// The download idempotency ledger (idempotency-keys pattern), keyed by
/// <c>drive:&lt;fileId&gt;:&lt;md5&gt;</c>. Enforces recording-ingest's two rules:
/// <list type="bullet">
///   <item>Replay of a seen key ⇒ logged no-op (no re-download).</item>
///   <item>A modified file (same fileId, NEW md5) ⇒ a distinct key, so the new
///   bytes are fetched and supersede the prior.</item>
/// </list>
/// Note the key includes the md5, so "already seen" is per-content, not per-file:
/// changing content changes the key, which is exactly the supersede semantics.
/// </summary>
public sealed class IdempotencyLedger
{
    private readonly IStateStore _store;
    private readonly ILogger _log;

    public IdempotencyLedger(IStateStore store, ILogger<IdempotencyLedger> log)
    {
        _store = store;
        _log = log;
    }

    /// <summary>True iff <paramref name="driveKey"/> is already recorded as a completed download.</summary>
    public async Task<bool> IsSeenAsync(string driveKey, CancellationToken ct)
    {
        var rec = await _store.GetDownloadAsync(driveKey, ct);
        return rec is { State: PipelineState.Queued or PipelineState.Normalized };
    }

    /// <summary>
    /// Returns true if this change should be downloaded now; false (a logged no-op)
    /// if <c>drive:&lt;fileId&gt;:&lt;md5&gt;</c> was already completed (replay).
    /// </summary>
    public async Task<bool> ShouldDownloadAsync(DriveChange change, CancellationToken ct)
    {
        if (await IsSeenAsync(change.DriveKey, ct))
        {
            _log.LogInformation("skip replay of already-staged key {DriveKey}", change.DriveKey);
            return false;
        }
        return true;
    }

    /// <summary>Record a completed download under its key.</summary>
    public Task RecordDownloadedAsync(
        DriveChange change, string kind, string stagedPath, string sha256, CancellationToken ct) =>
        _store.UpsertDownloadAsync(new DownloadRecord(
            DriveKey: change.DriveKey,
            FileId: change.FileId,
            Md5: change.Md5,
            Kind: kind,
            StagedPath: stagedPath,
            Sha256: sha256,
            State: PipelineState.Queued,
            Reason: null), ct);

    /// <summary>Record a failed download (retryable/terminal) under its key, with a reason.</summary>
    public Task RecordFailureAsync(
        DriveChange change, string kind, PipelineState failState, string reason, CancellationToken ct) =>
        _store.UpsertDownloadAsync(new DownloadRecord(
            DriveKey: change.DriveKey,
            FileId: change.FileId,
            Md5: change.Md5,
            Kind: kind,
            StagedPath: null,
            Sha256: null,
            State: failState,
            Reason: reason), ct);
}
