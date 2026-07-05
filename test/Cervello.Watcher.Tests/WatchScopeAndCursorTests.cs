using Cervello.Watcher;
using Cervello.Watcher.Domain;
using Xunit;

namespace Cervello.Watcher.Tests;

/// <summary>
/// drive-watch — folder-scoped detection + durable cursor resumption.
/// </summary>
public sealed class WatchScopeAndCursorTests
{
    private const string FolderId = "folder-recordings";

    // ---- Scenario: New in-boundary file is detected ----
    [Fact]
    public async Task In_boundary_file_is_detected_and_processed()
    {
        using var ws = new TempWorkspace();
        var h = new WorkerHarness(ws, FolderId);
        var audio = System.Text.Encoding.UTF8.GetBytes("audio-bytes");
        var txt = System.Text.Encoding.UTF8.GetBytes("hello transcript");
        h.Drive.SeedFile("A", "Foo.m4a", "audio/mp4", audio, parents: new[] { FolderId });
        h.Drive.SeedFile("T", "Foo.txt", "text/plain", txt, parents: new[] { FolderId });
        h.Drive.QueuePage(new[]
        {
            h.Drive.Meta("A"),
            h.Drive.Meta("T"),
        });

        await h.Worker.RunCycleAsync(default);

        // Both in-boundary files were downloaded and the pair produced a manifest entry.
        Assert.Contains("A", h.Drive.DownloadedFileIds);
        Assert.Contains("T", h.Drive.DownloadedFileIds);
        Assert.True(File.Exists(ws.ManifestPath));
        Assert.Equal(1, h.Worker.RecordingsNormalized);
    }

    // ---- Scenario: Out-of-boundary change is ignored ----
    [Fact]
    public async Task Out_of_boundary_change_is_ignored_but_cursor_advances()
    {
        using var ws = new TempWorkspace();
        var h = new WorkerHarness(ws, FolderId);
        var elsewhere = new DriveChange("X", "Other.m4a", "audio/mp4", null, 3,
            DateTimeOffset.Parse("2026-07-05T09:30:00Z"), DateTimeOffset.Parse("2026-07-05T09:30:00Z"),
            parents: new[] { "some-other-folder" }, removed: false, trashed: false);
        h.Drive.SeedFile("X", "Other.m4a", "audio/mp4", new byte[] { 1, 2, 3 }, parents: new[] { "some-other-folder" });
        h.Drive.QueuePage(new[] { elsewhere }, newStartPageToken: "next-token");

        await h.Worker.RunCycleAsync(default);

        Assert.False(h.Worker.IsInBoundary(elsewhere));
        Assert.DoesNotContain("X", h.Drive.DownloadedFileIds);
        // Cursor advanced past the ignored change.
        Assert.Equal("next-token", await h.State.GetCursorAsync(WatchWorker.CursorScope, default));
    }

    // ---- Scenario: Cursor bootstraps on first run ----
    [Fact]
    public async Task Cursor_bootstraps_on_first_run()
    {
        using var ws = new TempWorkspace();
        var h = new WorkerHarness(ws, FolderId);
        h.Drive.SetStartToken("bootstrap-token");
        // No queued page: an empty first poll. The bootstrap token must be persisted.
        Assert.Null(await h.State.GetCursorAsync(WatchWorker.CursorScope, default));

        await h.Worker.RunCycleAsync(default);

        var persisted = await h.State.GetCursorAsync(WatchWorker.CursorScope, default);
        Assert.NotNull(persisted); // bootstrapped + persisted
    }

    // ---- Scenario: Restart resumes from the persisted cursor ----
    [Fact]
    public async Task Restart_resumes_from_persisted_cursor_not_a_fresh_start()
    {
        using var ws = new TempWorkspace();
        var h = new WorkerHarness(ws, FolderId);
        // Simulate a prior run that persisted token "T".
        await h.State.SetCursorAsync(WatchWorker.CursorScope, "T", default);
        h.Drive.SetStartToken("FRESH-should-not-be-used");

        string? requestedFrom = null;
        var spy = new RecordingDriveSpy(h.Drive, tok => requestedFrom = tok);
        var worker = new WatchWorker(h.Config, spy, h.State, h.Downloader, h.Normalizer, h.Manifest, h.Ready,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WatchWorker>.Instance);
        worker.SetFolderId(FolderId);

        await worker.RunCycleAsync(default);

        Assert.Equal("T", requestedFrom); // resumed from T, not the fresh start token
    }

    // ---- Scenario: Cursor does not advance past an unprocessed batch ----
    [Fact]
    public async Task Cursor_does_not_advance_when_batch_processing_throws()
    {
        using var ws = new TempWorkspace();
        var h = new WorkerHarness(ws, FolderId);
        await h.State.SetCursorAsync(WatchWorker.CursorScope, "T-before", default);

        var audio = System.Text.Encoding.UTF8.GetBytes("bytes");
        var txt = System.Text.Encoding.UTF8.GetBytes("t");
        h.Drive.SeedFile("A", "Foo.m4a", "audio/mp4", audio, parents: new[] { FolderId });
        h.Drive.SeedFile("T", "Foo.txt", "text/plain", txt, parents: new[] { FolderId });
        h.Drive.QueuePage(new[]
        {
            h.Drive.Meta("A"),
            h.Drive.Meta("T"),
        }, newStartPageToken: "T-after");

        // Inject a manifest store that throws when the pair reaches registration —
        // a batch-processing failure BEFORE the batch completes.
        var throwingManifest = new ThrowingManifestStore();
        var worker = new WatchWorker(h.Config, h.Drive, h.State, h.Downloader, h.Normalizer,
            throwingManifest, h.Ready,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WatchWorker>.Instance);
        worker.SetFolderId(FolderId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.RunCycleAsync(default));

        // The cursor is UNCHANGED — the same batch re-runs next poll (at-least-once).
        Assert.Equal("T-before", await h.State.GetCursorAsync(WatchWorker.CursorScope, default));
    }
}
