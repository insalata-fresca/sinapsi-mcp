using Cervello.Watcher.Domain;
using Cervello.Watcher.Drive;
using Cervello.Watcher.Ingest;
using Cervello.Watcher.Normalize;
using Cervello.Watcher.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cervello.Watcher;

/// <summary>
/// The WATCH → NORMALIZE poll loop (polling-watcher pattern). Like
/// TimerOnlyIndexWorker it is the "no-NATS binary" shape: it references neither
/// Sinapsi.Nats nor any NATS client (invariant 3 / D8).
///
/// One cycle: list changes from the persisted cursor → filter to the
/// <c>cervello/recordings</c> folderId client-side (D3) → classify + download
/// (idempotent) → pair by basename → normalize → append one manifest entry →
/// mark ready locally. The cursor advances ONLY after the batch fully processes
/// (at-least-once); a throw mid-batch leaves it unchanged so the batch re-runs.
/// </summary>
public sealed class WatchWorker : BackgroundService
{
    internal const string CursorScope = "recordings";

    private readonly WatcherConfig _cfg;
    private readonly IDriveClient _drive;
    private readonly IStateStore _state;
    private readonly Downloader _downloader;
    private readonly Normalizer _normalizer;
    private readonly IManifestStore _manifest;
    private readonly ReadyMarker _ready;
    private readonly ILogger<WatchWorker> _log;

    // Resolved once and cached: the Drive folderId for cfg.FolderPath (D3 scope filter).
    private string? _folderId;

    // In-process pairer state (survives across cycles within a process run).
    private readonly Pairer _pairer = new();

    public bool Ready { get; private set; }
    public long RecordingsNormalized { get; private set; }

    public WatchWorker(
        WatcherConfig cfg,
        IDriveClient drive,
        IStateStore state,
        Downloader downloader,
        Normalizer normalizer,
        IManifestStore manifest,
        ReadyMarker ready,
        ILogger<WatchWorker> log)
    {
        _cfg = cfg;
        _drive = drive;
        _state = state;
        _downloader = downloader;
        _normalizer = normalizer;
        _manifest = manifest;
        _ready = ready;
        _log = log;
    }

    /// <summary>Set the resolved recordings folderId (D3). In prod, resolved from the folder path.</summary>
    public void SetFolderId(string folderId) => _folderId = folderId;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("cervello-watcher starting (no NATS; poll {Interval}s)", _cfg.PollIntervalSeconds);
        await _state.EnsureSchemaAsync(ct);

        // One-time backlog backfill BEFORE the poll loop (runs once per process start).
        await BackfillIfNeededAsync(ct);

        Ready = true;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                // A cycle failure leaves the cursor unchanged (see RunCycleAsync) — the
                // batch re-runs next poll. Log and back off to the next interval.
                _log.LogError(e, "watch cycle failed; cursor unchanged, retrying next poll");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(_cfg.PollIntervalSeconds), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// One WATCH → NORMALIZE cycle. Cursor discipline: the persisted cursor is read
    /// at the start and advanced ONLY after the whole batch is processed without
    /// throwing. A throw propagates with the cursor unchanged.
    /// </summary>
    public async Task RunCycleAsync(CancellationToken ct)
    {
        // 1. Bootstrap or resume the cursor.
        var token = await _state.GetCursorAsync(CursorScope, ct);
        if (token is null)
        {
            token = await _drive.GetStartPageTokenAsync(ct);
            await _state.SetCursorAsync(CursorScope, token, ct); // persist BEFORE first poll returns
            _log.LogInformation("cursor bootstrapped at {Token}", token);
        }

        // 2. List one page of changes from the current cursor.
        var page = await _drive.ListChangesAsync(token, ct);

        // 3. Folder-scope filter (D3) + classify + process the batch. Any throw here
        //    aborts the cycle WITHOUT advancing the cursor.
        foreach (var change in page.Changes)
        {
            if (!IsInBoundary(change))
                continue; // out-of-boundary: ignored, but the cursor still advances past it
            if (change.Removed || change.Trashed)
                continue;

            await ProcessChangeAsync(change, ct);
        }

        // 4. Advance the cursor ONLY now that the batch fully processed (at-least-once).
        var next = page.NewStartPageToken ?? page.NextPageToken;
        if (next is not null && next != token)
            await _state.SetCursorAsync(CursorScope, next, ct);
    }

    /// <summary>
    /// One-time startup BACKFILL of the pre-existing recording backlog (runs ONCE per
    /// process start, guarded by the caller placing it before the poll loop). Fires when:
    /// <list type="bullet">
    /// <item>the cursor is null — a genuine first run: the folder already holds recordings
    /// created before the watcher ever started, which the changes feed (bootstrapped at
    /// "now") will never surface; OR</item>
    /// <item><see cref="WatcherConfig.ForceBackfill"/> is true — a re-scan on an existing
    /// cursor to import a backlog that predates the current cursor (the operator's case:
    /// dozens of recordings, only the 3 modified-after-bootstrap ones imported).</item>
    /// </list>
    /// Every file goes through the SAME <see cref="ProcessChangeAsync"/> path as the change
    /// feed (download→classify→pair→<see cref="NormalizeAndRegisterAsync"/>), so it is fully
    /// idempotent: already-imported recordings dedupe on rec:&lt;id&gt;:&lt;sha&gt; and the
    /// manifest id, registering 0 new. Because ALL files are offered to the in-process
    /// <see cref="Pairer"/> before the scan returns, every basename gets both its audio +
    /// transcript sides → all complete pairs register; unpaired singletons stay pending.
    /// AFTER the scan the changes cursor is bootstrapped (if absent) so incremental polling
    /// picks up NEW files normally.
    /// </summary>
    internal async Task BackfillIfNeededAsync(CancellationToken ct)
    {
        if (_folderId is null)
        {
            // Prod resolves the folderId at startup before the worker runs; a null here
            // means IsInBoundary would reject everything anyway — nothing to backfill.
            _log.LogWarning("backfill skipped: recordings folderId not resolved");
            return;
        }

        var cursor = await _state.GetCursorAsync(CursorScope, ct);
        var firstRun = cursor is null;
        if (!firstRun && !_cfg.ForceBackfill)
            return; // cursor already established and no force → incremental-only, no re-scan

        _log.LogInformation(
            "backfill starting (reason: {Reason}) — scanning full folder {FolderId}",
            firstRun ? "first-run/null-cursor" : "force-backfill", _folderId);

        var files = await _drive.ListFolderAsync(_folderId, ct);

        var before = RecordingsNormalized;
        var pairs = 0;
        foreach (var change in files)
        {
            if (!IsInBoundary(change) || change.Removed || change.Trashed)
                continue;
            var registeredBefore = RecordingsNormalized;
            await ProcessChangeAsync(change, ct);
            if (RecordingsNormalized > registeredBefore)
                pairs++;
        }

        var newlyRegistered = RecordingsNormalized - before;
        _log.LogInformation(
            "backfill: scanned {Scanned} files, {Pairs} pairs, {New} new recordings registered",
            files.Count, pairs, newlyRegistered);

        // Bootstrap the changes cursor (if absent) so incremental polling continues normally
        // for NEW files. On a force-backfill with an existing cursor, keep the cursor as-is
        // (the snapshot it carries stays valid — the backfill was the reconciliation).
        if (firstRun)
        {
            var token = await _drive.GetStartPageTokenAsync(ct);
            await _state.SetCursorAsync(CursorScope, token, ct);
            _log.LogInformation("cursor bootstrapped after backfill at {Token}", token);
        }
    }

    /// <summary>Download the change, pair it, and (if paired) normalize + register.</summary>
    private async Task ProcessChangeAsync(DriveChange change, CancellationToken ct)
    {
        var outcome = await _downloader.StageAsync(change, ct);
        if (!outcome.Staged)
            return; // replay no-op / failure / non-audio — nothing to pair yet

        var staged = new StagedFile(
            Basename: Pairer.BasenameOf(change.Name ?? change.FileId),
            Kind: outcome.Kind,
            FileId: change.FileId,
            Sha256: outcome.Sha256!,
            Change: change);

        var pair = _pairer.Offer(staged);
        if (pair is null)
        {
            _log.LogInformation("{Basename} pending — waiting for its {Missing}",
                staged.Basename, staged.Kind == "audio" ? "transcript" : "audio");
            return;
        }

        await NormalizeAndRegisterAsync(pair, ct);
    }

    /// <summary>Assign a deterministic id, dedupe, append one manifest entry, mark ready.</summary>
    public async Task NormalizeAndRegisterAsync(PairedRecording pair, CancellationToken ct)
    {
        var recording = _normalizer.Normalize(pair);

        // Dedupe by rec:<id>:<audio_sha256> — a known recording is a no-op.
        if (await _state.RecordingExistsAsync(recording.RecordingKey, ct))
        {
            _log.LogInformation("recording {Key} already registered — no-op", recording.RecordingKey);
            // The manifest append is itself idempotent (id dedupe) — still safe to call,
            // but we short-circuit to keep the file byte-unchanged deterministically.
            return;
        }

        var entry = ManifestEntry.ForRecording(recording);
        var appended = await _manifest.AppendAsync(entry, ct);
        await _state.UpsertRecordingAsync(recording, ct);
        _ready.Mark(recording.Id); // LOCAL marker only (invariant 3)
        if (appended)
            RecordingsNormalized++;
        _log.LogInformation("normalized {Id} (manifest {Result})",
            recording.Id, appended ? "appended" : "already-present");
    }

    /// <summary>Client-side boundary check (D3): keep only changes parented by the recordings folder.</summary>
    internal bool IsInBoundary(DriveChange change)
    {
        // No folder resolved yet ⇒ accept nothing (fail-closed; prod resolves it at startup).
        if (_folderId is null)
            return false;
        return change.Parents.Contains(_folderId);
    }
}
