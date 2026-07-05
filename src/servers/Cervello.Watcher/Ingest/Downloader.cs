using System.Net;
using Cervello.Watcher.Domain;
using Cervello.Watcher.Drive;
using Microsoft.Extensions.Logging;

namespace Cervello.Watcher.Ingest;

/// <summary>Result of staging one change: either staged bytes, a skipped replay, or a failure.</summary>
public sealed record DownloadOutcome(
    DriveChange Change,
    string Kind,
    string? StagedPath,
    string? Sha256,
    PipelineState State,
    string? Reason)
{
    public bool Staged => State is PipelineState.Queued && StagedPath is not null;
}

/// <summary>
/// Downloads an in-boundary change's bytes into staging, keyed by the ledger
/// (recording-ingest). First sight ⇒ fetch + stage + record; replay ⇒ logged
/// no-op; transient error ⇒ <c>failed_retryable</c>; terminal ⇒ <c>failed_terminal</c>
/// with a reason. Streams via <see cref="IDriveClient"/> (proxy-routed in prod).
/// </summary>
public sealed class Downloader
{
    private readonly IDriveClient _drive;
    private readonly BlobStore _blobs;
    private readonly IdempotencyLedger _ledger;
    private readonly ILogger _log;

    public Downloader(IDriveClient drive, BlobStore blobs, IdempotencyLedger ledger, ILogger<Downloader> log)
    {
        _drive = drive;
        _blobs = blobs;
        _ledger = ledger;
        _log = log;
    }

    public static string ClassifyKind(DriveChange c)
    {
        var name = c.Name ?? "";
        if (name.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase)) return "audio";
        if (name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return "transcript";
        return "other";
    }

    public async Task<DownloadOutcome> StageAsync(DriveChange change, CancellationToken ct)
    {
        var kind = ClassifyKind(change);
        if (kind == "other")
        {
            const string reason = "non-audio/non-transcript file";
            await _ledger.RecordFailureAsync(change, kind, PipelineState.FailedTerminal, reason, ct);
            return new DownloadOutcome(change, kind, null, null, PipelineState.FailedTerminal, reason);
        }

        // Replay: an already-completed key is a logged no-op — return the prior staged row.
        if (!await _ledger.ShouldDownloadAsync(change, ct))
        {
            // The existing record already carries the staged path/sha (queried by the caller if needed).
            return new DownloadOutcome(change, kind, null, null, PipelineState.Queued, "replay-skipped");
        }

        try
        {
            using var ms = new MemoryStream();
            await _drive.DownloadMediaAsync(change.FileId, ms, ct);
            var bytes = ms.ToArray();
            var ext = kind == "audio" ? ".m4a" : ".txt";
            var (path, sha) = _blobs.Write(bytes, ext);
            await _ledger.RecordDownloadedAsync(change, kind, path, sha, ct);
            _log.LogInformation("staged {Kind} {DriveKey} -> sha256 {Sha}", kind, change.DriveKey, sha);
            return new DownloadOutcome(change, kind, path, sha, PipelineState.Queued, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (IsTransient(e))
        {
            var reason = $"transient: {e.GetType().Name}: {e.Message}";
            await _ledger.RecordFailureAsync(change, kind, PipelineState.FailedRetryable, reason, ct);
            _log.LogWarning(e, "retryable download failure for {DriveKey}", change.DriveKey);
            return new DownloadOutcome(change, kind, null, null, PipelineState.FailedRetryable, reason);
        }
        catch (Exception e)
        {
            var reason = $"terminal: {e.GetType().Name}: {e.Message}";
            await _ledger.RecordFailureAsync(change, kind, PipelineState.FailedTerminal, reason, ct);
            _log.LogError(e, "terminal download failure for {DriveKey}", change.DriveKey);
            return new DownloadOutcome(change, kind, null, null, PipelineState.FailedTerminal, reason);
        }
    }

    /// <summary>
    /// Transient = worth retrying under the same key: 5xx, request timeout, or a
    /// raw transport error. Everything else (404, malformed) is terminal.
    /// </summary>
    internal static bool IsTransient(Exception e)
    {
        switch (e)
        {
            case TimeoutException:
            case TaskCanceledException: // HttpClient timeout surfaces as this
                return true;
            case HttpRequestException hre:
                if (hre.StatusCode is HttpStatusCode s)
                    return (int)s >= 500 && (int)s <= 599;
                return true; // no status ⇒ transport-level ⇒ retry
            case Google.GoogleApiException gae:
                var code = (int)gae.HttpStatusCode;
                return code >= 500 && code <= 599;
        }
        return false;
    }
}
