using Cervello.Watcher.Domain;
using Cervello.Watcher.Drive;
using Cervello.Watcher.Ingest;
using Cervello.Watcher.State;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cervello.Watcher.Tests;

/// <summary>recording-ingest — idempotent download, custody, failure classification.</summary>
public sealed class IngestTests
{
    private const string FolderId = "folder-recordings";

    private static DriveChange Change(string id, string name, byte[] bytes, string? md5 = null) =>
        new(id, name, name.EndsWith(".m4a") ? "audio/mp4" : "text/plain",
            md5: md5 ?? Convert.ToHexString(System.Security.Cryptography.MD5.HashData(bytes)).ToLowerInvariant(),
            size: bytes.LongLength,
            createdTime: DateTimeOffset.Parse("2026-07-05T09:30:00Z"),
            modifiedTime: DateTimeOffset.Parse("2026-07-05T09:30:00Z"),
            parents: new[] { FolderId }, removed: false, trashed: false);

    // ---- Scenario: First download of a new file ----
    [Fact]
    public async Task First_download_stages_bytes_and_records_the_key()
    {
        using var ws = new TempWorkspace();
        var h = new WorkerHarness(ws);
        var bytes = System.Text.Encoding.UTF8.GetBytes("brand new audio");
        h.Drive.SeedFile("F", "Foo.m4a", "audio/mp4", bytes, parents: new[] { FolderId });
        var change = Change("F", "Foo.m4a", bytes);

        var outcome = await h.Downloader.StageAsync(change, default);

        Assert.True(outcome.Staged);
        Assert.Equal(PipelineState.Queued, outcome.State);
        Assert.NotNull(outcome.StagedPath);
        Assert.Equal(BlobStore.Sha256Hex(bytes), outcome.Sha256);
        // Ledger recorded drive:F:<md5>.
        var rec = await h.State.GetDownloadAsync(change.DriveKey, default);
        Assert.NotNull(rec);
        Assert.Equal("F", rec!.FileId);
    }

    // ---- Scenario: Replay of a seen key is a no-op ----
    [Fact]
    public async Task Replay_of_a_seen_key_is_a_no_op()
    {
        using var ws = new TempWorkspace();
        var h = new WorkerHarness(ws);
        var bytes = System.Text.Encoding.UTF8.GetBytes("audio");
        h.Drive.SeedFile("F", "Foo.m4a", "audio/mp4", bytes, parents: new[] { FolderId });
        var change = Change("F", "Foo.m4a", bytes);

        await h.Downloader.StageAsync(change, default); // first
        var callsAfterFirst = h.Drive.DownloadCallCount;

        var replay = await h.Downloader.StageAsync(change, default); // replay same key

        Assert.False(replay.Staged);
        Assert.Equal("replay-skipped", replay.Reason);
        Assert.Equal(callsAfterFirst, h.Drive.DownloadCallCount); // no re-download
    }

    // ---- Scenario: Modified file supersedes prior bytes ----
    [Fact]
    public async Task Modified_file_new_md5_is_re_downloaded_and_supersedes()
    {
        using var ws = new TempWorkspace();
        var h = new WorkerHarness(ws);
        var v1 = System.Text.Encoding.UTF8.GetBytes("version one");
        h.Drive.SeedFile("F", "Foo.m4a", "audio/mp4", v1, parents: new[] { FolderId });
        var c1 = Change("F", "Foo.m4a", v1);
        await h.Downloader.StageAsync(c1, default);
        var afterV1 = h.Drive.DownloadCallCount;

        // Same fileId, new bytes => new md5 => new key => re-download.
        var v2 = System.Text.Encoding.UTF8.GetBytes("version two is different");
        // Reseed the fake's bytes so the download returns v2.
        h.Drive.SeedFile("F", "Foo.m4a", "audio/mp4", v2, parents: new[] { FolderId });
        var c2 = Change("F", "Foo.m4a", v2);

        Assert.NotEqual(c1.DriveKey, c2.DriveKey); // different key (md5 changed)
        var outcome = await h.Downloader.StageAsync(c2, default);

        Assert.True(outcome.Staged);
        Assert.Equal(afterV1 + 1, h.Drive.DownloadCallCount); // re-downloaded
        Assert.Equal(BlobStore.Sha256Hex(v2), outcome.Sha256); // reflects the new bytes
    }

    // ---- Scenario: Transient error is retryable ----
    [Fact]
    public async Task Transient_5xx_marks_failed_retryable()
    {
        using var ws = new TempWorkspace();
        var h = new WorkerHarness(ws);
        var bytes = System.Text.Encoding.UTF8.GetBytes("x");
        h.Drive.SeedFile("F", "Foo.m4a", "audio/mp4", bytes, parents: new[] { FolderId });
        h.Drive.DownloadFaults["F"] = new DriveMediaException("503", transient: true);
        var change = Change("F", "Foo.m4a", bytes);

        var outcome = await h.Downloader.StageAsync(change, default);

        Assert.Equal(PipelineState.FailedRetryable, outcome.State);
        var rec = await h.State.GetDownloadAsync(change.DriveKey, default);
        Assert.Equal(PipelineState.FailedRetryable, rec!.State);
        // A later poll retries under the SAME key (ledger did not mark it seen).
        Assert.False(await h.Ledger.IsSeenAsync(change.DriveKey, default));
    }

    [Fact]
    public void Timeout_is_classified_transient()
    {
        Assert.True(Downloader.IsTransient(new TaskCanceledException()));
        Assert.True(Downloader.IsTransient(new TimeoutException()));
        Assert.True(Downloader.IsTransient(new HttpRequestException("no status"))); // transport-level
    }

    // ---- Scenario: Terminal error carries a reason ----
    [Fact]
    public async Task Terminal_error_carries_a_nonempty_reason()
    {
        using var ws = new TempWorkspace();
        var h = new WorkerHarness(ws);
        var bytes = System.Text.Encoding.UTF8.GetBytes("x");
        h.Drive.SeedFile("F", "Foo.m4a", "audio/mp4", bytes, parents: new[] { FolderId });
        h.Drive.DownloadFaults["F"] = new InvalidOperationException("malformed media");
        var change = Change("F", "Foo.m4a", bytes);

        var outcome = await h.Downloader.StageAsync(change, default);

        Assert.Equal(PipelineState.FailedTerminal, outcome.State);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Reason));
        var rec = await h.State.GetDownloadAsync(change.DriveKey, default);
        Assert.Equal(PipelineState.FailedTerminal, rec!.State);
        Assert.False(string.IsNullOrWhiteSpace(rec.Reason));
    }

    [Fact]
    public async Task Non_audio_non_transcript_is_terminal_with_reason()
    {
        using var ws = new TempWorkspace();
        var h = new WorkerHarness(ws);
        var bytes = System.Text.Encoding.UTF8.GetBytes("x");
        var change = Change("F", "Foo.pdf", bytes);

        var outcome = await h.Downloader.StageAsync(change, default);

        Assert.Equal(PipelineState.FailedTerminal, outcome.State);
        Assert.Equal("other", outcome.Kind);
        Assert.Equal(0, h.Drive.DownloadCallCount); // never fetched
    }

    // ---- Idempotency ledger unit: replay no-op + modified supersede at the key level ----
    [Fact]
    public async Task Ledger_replay_is_noop_and_modified_is_a_distinct_key()
    {
        var store = new InMemoryStateStore();
        var ledger = new IdempotencyLedger(store, NullLogger<IdempotencyLedger>.Instance);
        var a = Change("F", "Foo.m4a", System.Text.Encoding.UTF8.GetBytes("A"));

        Assert.True(await ledger.ShouldDownloadAsync(a, default)); // unseen
        await ledger.RecordDownloadedAsync(a, "audio", "/staging/a", "sha-a", default);
        Assert.False(await ledger.ShouldDownloadAsync(a, default)); // replay no-op

        var b = Change("F", "Foo.m4a", System.Text.Encoding.UTF8.GetBytes("B-different"));
        Assert.NotEqual(a.DriveKey, b.DriveKey);
        Assert.True(await ledger.ShouldDownloadAsync(b, default)); // new md5 => new key => fetch
    }
}
