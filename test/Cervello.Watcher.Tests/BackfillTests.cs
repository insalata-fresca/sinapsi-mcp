using Cervello.Watcher;
using Cervello.Watcher.State;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cervello.Watcher.Tests;

/// <summary>
/// WATCHER-BACKLOG-BACKFILL — the one-time startup scan that imports the
/// PRE-EXISTING recording backlog (files created before the watcher ever ran, which
/// the changes cursor — bootstrapped at "now" — never surfaces). Fires on a genuine
/// first run (null cursor) OR when ForceBackfill is set, runs every folder file
/// through the SAME download→pair→register path, is fully idempotent, and bootstraps
/// the changes cursor afterward so incremental polling continues.
/// </summary>
public sealed class BackfillTests
{
    private const string FolderId = "folder-recordings";

    /// <summary>Seed N complete audio+transcript pairs into the folder, all pre-dating the watcher.</summary>
    private static void SeedPairs(WorkerHarness h, int n)
    {
        for (var i = 0; i < n; i++)
        {
            var audio = System.Text.Encoding.UTF8.GetBytes($"audio-{i}");
            var txt = System.Text.Encoding.UTF8.GetBytes($"transcript-{i}");
            h.Drive.SeedFile($"A{i}", $"Rec{i}.m4a", "audio/mp4", audio, parents: new[] { FolderId });
            h.Drive.SeedFile($"T{i}", $"Rec{i}.txt", "text/plain", txt, parents: new[] { FolderId });
        }
    }

    // (a) The backfill processes the full folder and registers all complete pairs.
    [Fact]
    public async Task Backfill_on_null_cursor_registers_all_backlog_pairs()
    {
        using var ws = new TempWorkspace();
        var h = new WorkerHarness(ws, FolderId);
        SeedPairs(h, 5);

        // Null cursor == genuine first run.
        Assert.Null(await h.State.GetCursorAsync(WatchWorker.CursorScope, default));

        await h.Worker.BackfillIfNeededAsync(default);

        Assert.Equal(5, h.Worker.RecordingsNormalized);
        Assert.True(File.Exists(ws.ManifestPath));
        // Every backlog file was downloaded — both sides of every pair.
        Assert.Equal(10, h.Drive.DownloadedFileIds.Count);
    }

    // (d) After the backfill on a first run, the changes cursor is bootstrapped so
    //     incremental polling continues normally for NEW files.
    [Fact]
    public async Task Backfill_first_run_bootstraps_the_cursor_afterward()
    {
        using var ws = new TempWorkspace();
        var h = new WorkerHarness(ws, FolderId);
        h.Drive.SetStartToken("bootstrap-token");
        SeedPairs(h, 2);

        await h.Worker.BackfillIfNeededAsync(default);

        var persisted = await h.State.GetCursorAsync(WatchWorker.CursorScope, default);
        Assert.NotNull(persisted); // incremental polling can now resume from a real cursor
    }

    // (b) The backfill is idempotent on a second run — 0 new recordings registered.
    [Fact]
    public async Task Backfill_is_idempotent_on_a_second_run()
    {
        using var ws = new TempWorkspace();
        var h = new WorkerHarness(ws, FolderId);
        SeedPairs(h, 3);

        await h.Worker.BackfillIfNeededAsync(default); // first run: registers 3
        Assert.Equal(3, h.Worker.RecordingsNormalized);

        // Force a re-scan (cursor now exists) — every recording dedupes on rec:<id>:<sha>.
        var forced = new WatchWorker(
            h.Config with { ForceBackfill = true },
            h.Drive, h.State, h.Downloader, h.Normalizer, h.Manifest, h.Ready,
            NullLogger<WatchWorker>.Instance);
        forced.SetFolderId(FolderId);

        await forced.BackfillIfNeededAsync(default);

        Assert.Equal(0, forced.RecordingsNormalized); // 0 NEW — all were already registered
    }

    // (c) ForceBackfill runs the scan even when a cursor already exists.
    [Fact]
    public async Task ForceBackfill_runs_even_with_an_existing_cursor()
    {
        using var ws = new TempWorkspace();
        var h = new WorkerHarness(ws, FolderId);
        // Simulate a prior run that already bootstrapped a cursor at "now" and imported nothing.
        await h.State.SetCursorAsync(WatchWorker.CursorScope, "cursor-at-now", default);
        SeedPairs(h, 4);

        var forced = new WatchWorker(
            h.Config with { ForceBackfill = true },
            h.Drive, h.State, h.Downloader, h.Normalizer, h.Manifest, h.Ready,
            NullLogger<WatchWorker>.Instance);
        forced.SetFolderId(FolderId);

        await forced.BackfillIfNeededAsync(default);

        Assert.Equal(4, forced.RecordingsNormalized); // the backlog was imported despite the cursor
        // Force-backfill on an existing cursor keeps it as-is (the snapshot stays valid).
        Assert.Equal("cursor-at-now", await h.State.GetCursorAsync(WatchWorker.CursorScope, default));
    }

    // Guard: with an existing cursor and NO force, the backfill is a no-op (incremental-only).
    [Fact]
    public async Task No_backfill_when_cursor_exists_and_force_is_off()
    {
        using var ws = new TempWorkspace();
        var h = new WorkerHarness(ws, FolderId); // Config default ForceBackfill = false
        await h.State.SetCursorAsync(WatchWorker.CursorScope, "existing", default);
        SeedPairs(h, 3);

        await h.Worker.BackfillIfNeededAsync(default);

        Assert.Equal(0, h.Worker.RecordingsNormalized);
        Assert.Empty(h.Drive.DownloadedFileIds); // nothing scanned
    }

    // MIXED-cases: complete pairs register AND single-sided files flush as singleton recordings.
    // (Was: "leaves singletons pending" — now they import as audio-only / transcript-only.)
    [Fact]
    public async Task Backfill_registers_complete_pairs_and_flushes_singletons_as_recordings()
    {
        using var ws = new TempWorkspace();
        var h = new WorkerHarness(ws, FolderId);
        SeedPairs(h, 2); // 2 complete pairs
        // A lone audio with no transcript sibling → an audio-only recording.
        h.Drive.SeedFile("A-lonely", "Lonely.m4a", "audio/mp4",
            System.Text.Encoding.UTF8.GetBytes("lonely"), parents: new[] { FolderId });
        // A lone transcript with no audio sibling → a transcript-only recording.
        h.Drive.SeedFile("T-orphan", "Orphan.txt", "text/plain",
            System.Text.Encoding.UTF8.GetBytes("orphan transcript"), parents: new[] { FolderId });

        await h.Worker.BackfillIfNeededAsync(default);

        // 2 complete pairs + 1 audio-only + 1 transcript-only = 4 recordings registered (none dropped).
        Assert.Equal(4, h.Worker.RecordingsNormalized);
    }

    // A genuine pair whose two files are scanned across the same folder listing still PAIRS — it is
    // NOT flushed as two singletons (the flush runs only AFTER every file is offered).
    [Fact]
    public async Task Backfill_pairs_a_two_file_recording_and_does_not_flush_it_as_singletons()
    {
        using var ws = new TempWorkspace();
        var h = new WorkerHarness(ws, FolderId);
        SeedPairs(h, 1); // exactly one complete pair, both sides in the folder

        await h.Worker.BackfillIfNeededAsync(default);

        Assert.Equal(1, h.Worker.RecordingsNormalized); // ONE paired recording, not two singletons
    }

    // Idempotent: a second backfill over the same mixed folder registers 0 NEW recordings.
    [Fact]
    public async Task Backfill_with_singletons_is_idempotent_on_a_second_run()
    {
        using var ws = new TempWorkspace();
        var h = new WorkerHarness(ws, FolderId);
        SeedPairs(h, 1);
        h.Drive.SeedFile("A-lonely", "Lonely.m4a", "audio/mp4",
            System.Text.Encoding.UTF8.GetBytes("lonely"), parents: new[] { FolderId });
        h.Drive.SeedFile("T-orphan", "Orphan.txt", "text/plain",
            System.Text.Encoding.UTF8.GetBytes("orphan"), parents: new[] { FolderId });

        await h.Worker.BackfillIfNeededAsync(default); // 1 pair + 2 singletons = 3
        Assert.Equal(3, h.Worker.RecordingsNormalized);

        var forced = new WatchWorker(
            h.Config with { ForceBackfill = true },
            h.Drive, h.State, h.Downloader, h.Normalizer, h.Manifest, h.Ready,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WatchWorker>.Instance);
        forced.SetFolderId(FolderId);

        await forced.BackfillIfNeededAsync(default);

        Assert.Equal(0, forced.RecordingsNormalized); // all 3 already registered (pair + both singletons)
    }
}
