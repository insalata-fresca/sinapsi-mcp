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

    // MIXED-cases: how many INCREMENTAL cycles each still-pending (single-sided) basename has been
    // held. A singleton flushes only once its age reaches the grace window, so a pair whose two files
    // arrive in separate cycles still pairs first. Reset when a basename pairs / is flushed.
    private readonly Dictionary<string, int> _pendingAgeCycles = new(StringComparer.Ordinal);

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

        // 3b. MIXED-cases: at the END of the batch, flush files that have been held on ONE side only
        //     for at least the grace window (CERVELLO_WATCHER_SINGLETON_FLUSH_GRACE_CYCLES). The grace
        //     lets a genuine pair whose two files arrive in SEPARATE cycles still pair before either
        //     side is registered as a singleton; only files that stay single-sided across the grace
        //     window flush as audio-only / transcript-only recordings. (The pairer state survives
        //     across cycles — it is a field — so a later sibling completes the pair.)
        await FlushAgedSingletonsAsync(ct);

        // 4. Advance the cursor ONLY now that the batch fully processed (at-least-once).
        var next = page.NewStartPageToken ?? page.NextPageToken;
        if (next is not null && next != token)
            await _state.SetCursorAsync(CursorScope, next, ct);
    }

    /// <summary>
    /// Register every currently-UNPAIRED held staged file as a single-sided recording (audio-only or
    /// transcript-only) via the SAME <see cref="NormalizeAndRegisterAsync"/> path as a complete pair.
    /// Fully idempotent (each dedupes on its recording key). Returns the count of NEW singleton
    /// recordings registered this call. Call at the END of a scan/cycle, after every file was offered.
    /// </summary>
    internal async Task<long> FlushSingletonsAsync(CancellationToken ct)
    {
        var before = RecordingsNormalized;
        foreach (var single in _pairer.FlushSingletons())
        {
            _log.LogInformation("flushing singleton {Basename} as {Kind}-only recording",
                single.Basename, single.HasAudio ? "audio" : "transcript");
            await NormalizeAndRegisterAsync(single, ct);
            _pendingAgeCycles.Remove(single.Basename);
        }
        return RecordingsNormalized - before;
    }

    /// <summary>
    /// Incremental-path flush: age every still-pending single-sided basename by one cycle and flush
    /// only those that have waited at least <see cref="WatcherConfig.SingletonFlushGraceCycles"/>
    /// cycles (so a pair whose two files arrive in separate cycles still pairs first). A basename that
    /// paired in the meantime is no longer pending and its age counter is dropped. Idempotent.
    /// </summary>
    internal async Task<long> FlushAgedSingletonsAsync(CancellationToken ct)
    {
        var pending = new HashSet<string>(_pairer.Pending(), StringComparer.Ordinal);

        // Drop age counters for basenames that are no longer single-sided (they paired).
        foreach (var stale in _pendingAgeCycles.Keys.Where(k => !pending.Contains(k)).ToList())
            _pendingAgeCycles.Remove(stale);

        // Age every currently-pending basename by one cycle.
        foreach (var basename in pending)
            _pendingAgeCycles[basename] = _pendingAgeCycles.GetValueOrDefault(basename) + 1;

        var grace = _cfg.SingletonFlushGraceCycles;
        var before = RecordingsNormalized;
        foreach (var single in _pairer.FlushSingletons())
        {
            if (_pendingAgeCycles.GetValueOrDefault(single.Basename) < grace)
                continue; // still within the grace window — give the sibling a chance to arrive
            _log.LogInformation("flushing aged singleton {Basename} as {Kind}-only recording (waited {Age} cycles)",
                single.Basename, single.HasAudio ? "audio" : "transcript",
                _pendingAgeCycles.GetValueOrDefault(single.Basename));
            await NormalizeAndRegisterAsync(single, ct);
            _pendingAgeCycles.Remove(single.Basename);
        }
        return RecordingsNormalized - before;
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
        _log.LogInformation("backfill: gdrive listed {Listed} file(s) in folder {FolderId}",
            files.Count, _folderId);

        var before = RecordingsNormalized;
        // Coverage tally — surfaced in the mandatory end-of-scan summary so an under-processing scan
        // (rc33: only 1 of dozens staged) is diagnosable from the log alone, not a black box.
        int listed = files.Count, boundarySkipped = 0, audio = 0, transcript = 0,
            other = 0, replay = 0, failed = 0;

        foreach (var change in files)
        {
            if (!IsInBoundary(change) || change.Removed || change.Trashed)
            {
                boundarySkipped++;
                _log.LogDebug("scan skip {Name} ({FileId}): out-of-boundary/removed/trashed",
                    change.Name ?? change.FileId, change.FileId);
                continue;
            }

            // Per-file resilience: a single bad file (download failure, parse glitch, slow/timeout
            // gateway call whose retries were exhausted) must NOT abort the WHOLE scan — log it and
            // carry on so the other dozens still import. Only caller cancellation unwinds the scan.
            StageKind kind;
            try
            {
                kind = await ProcessChangeAsync(change, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                failed++;
                _log.LogWarning(e, "scan {Name} ({FileId}): processing failed — skipped (scan continues)",
                    change.Name ?? change.FileId, change.FileId);
                continue;
            }

            switch (kind)
            {
                case StageKind.Audio: audio++; break;
                case StageKind.Transcript: transcript++; break;
                case StageKind.Other: other++; break;
                case StageKind.ReplaySkipped: replay++; break;
                case StageKind.Failed: failed++; break;
            }
        }

        // MIXED-cases: EVERY folder file has now been offered to the pairer, so any file still held
        // on ONE side only is a genuine single-sided recording (audio-only or transcript-only) — NOT
        // a half-scanned pair. Flush them at the END of the scan so they import too (they were
        // silently dropped before). Idempotent: an already-registered singleton dedupes on its key.
        var singletons = await FlushSingletonsAsync(ct);

        var newlyRegistered = RecordingsNormalized - before;
        var heldPending = _pairer.Pending().Count;
        // MANDATORY end-of-scan summary — the observability the rc33 diagnosis needed. If listed is
        // large but audio+transcript are ~0, the LISTING or CLASSIFY is dropping files (see the
        // per-file scan DEBUG lines for the reason: 'other' = non-.m4a/.txt, 'replay' = already staged).
        _log.LogInformation(
            "backfill summary: listed {Listed}, boundary-skipped {BoundarySkipped}, staged {Audio} audio + {Transcript} transcripts, " +
            "other {Other}, replay {Replay}, failed {Failed}; registered {New} recordings ({Singletons} singletons), {Held} still held pending",
            listed, boundarySkipped, audio, transcript, other, replay, failed,
            newlyRegistered, singletons, heldPending);

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

    /// <summary>The classification of a scanned file, for the per-file trace + end-of-scan tally.</summary>
    internal enum StageKind { Audio, Transcript, Other, ReplaySkipped, Failed }

    /// <summary>
    /// Download the change, pair it, and (if paired) normalize + register. Returns HOW the file was
    /// classified/handled so the scan can tally coverage (audio / transcript / other / replay / fail)
    /// and make the drop-reasons observable — the rc33 backfill staged only 1 of dozens with NO
    /// visibility into why the rest never staged.
    /// </summary>
    private async Task<StageKind> ProcessChangeAsync(DriveChange change, CancellationToken ct)
    {
        var name = change.Name ?? change.FileId;
        var outcome = await _downloader.StageAsync(change, ct);
        if (!outcome.Staged)
        {
            // Make the drop reason explicit: a non-audio/non-transcript file ("other"), a replay
            // no-op, or a download failure. Previously ALL of these were a silent `return`.
            var kind = outcome.Kind switch
            {
                "other" => StageKind.Other,
                _ when outcome.Reason == "replay-skipped" => StageKind.ReplaySkipped,
                _ => StageKind.Failed,
            };
            _log.LogDebug("scan {Name} ({FileId}): not staged — kind={Kind} state={State} reason={Reason}",
                name, change.FileId, outcome.Kind, outcome.State, outcome.Reason ?? "(none)");
            return kind;
        }

        var staged = new StagedFile(
            Basename: Pairer.BasenameOf(name),
            Kind: outcome.Kind,
            FileId: change.FileId,
            Sha256: outcome.Sha256!,
            Change: change);
        _log.LogDebug("scan {Name} ({FileId}): staged {Kind} basename={Basename} sha={Sha}",
            name, change.FileId, staged.Kind, staged.Basename, staged.Sha256);

        var pair = _pairer.Offer(staged);
        if (pair is null)
        {
            _log.LogInformation("{Basename} pending — waiting for its {Missing}",
                staged.Basename, staged.Kind == "audio" ? "transcript" : "audio");
        }
        else
        {
            await NormalizeAndRegisterAsync(pair, ct);
        }
        return staged.Kind == "audio" ? StageKind.Audio : StageKind.Transcript;
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
