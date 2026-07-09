using Cervello.Watcher;
using Cervello.Watcher.Domain;
using Cervello.Watcher.Ingest;
using Xunit;

namespace Cervello.Watcher.Tests;

/// <summary>
/// End-to-end WATCH → NORMALIZE over the FakeDriveClient (tasks.md 10.1 / design
/// "Testing Approach"). Seed Foo.m4a + Foo.txt → one cycle → exactly one manifest
/// entry with matching checksums, state: normalized. A SECOND full cycle is a
/// byte-unchanged no-op.
/// </summary>
public sealed class EndToEndTests
{
    private const string FolderId = "folder-recordings";

    [Fact]
    public async Task Seed_pair_one_cycle_produces_one_entry_second_cycle_is_noop()
    {
        using var ws = new TempWorkspace();

        var audioBytes = System.Text.Encoding.UTF8.GetBytes("the audio bytes of Foo");
        var txtBytes = System.Text.Encoding.UTF8.GetBytes("Foo's raw google transcript");
        var expectedAudioSha = BlobStore.Sha256Hex(audioBytes);

        // ---- cycle 1 ----
        var h1 = new WorkerHarness(ws, FolderId);
        h1.Drive.SeedFile("A", "Foo.m4a", "audio/mp4", audioBytes,
            createdTime: DateTimeOffset.Parse("2026-07-05T09:30:00Z"), parents: new[] { FolderId });
        h1.Drive.SeedFile("T", "Foo.txt", "text/plain", txtBytes,
            createdTime: DateTimeOffset.Parse("2026-07-05T09:30:00Z"), parents: new[] { FolderId });
        h1.Drive.QueuePage(new[]
        {
            h1.Drive.Meta("A"),
            h1.Drive.Meta("T"),
        });

        await h1.Worker.RunCycleAsync(default);

        // Exactly one manifest entry, matching checksums + drive ids, state normalized.
        var text = File.ReadAllText(ws.ManifestPath);
        var entryLines = text.Split('\n').Where(l => l.TrimStart().StartsWith("- id:")).ToArray();
        Assert.Single(entryLines);
        Assert.Contains("- id: 20260705-foo", text);
        Assert.Contains($"audio_sha256: {expectedAudioSha}", text);
        Assert.Contains("source_drive_id: A", text);
        Assert.Contains("google_txt: T", text);
        Assert.Contains("attribution: pending", text);
        Assert.Contains("recorded_at: 2026-07-05T09:30", text);
        Assert.Contains("state: normalized", text);
        // No audio bytes leaked into the manifest.
        Assert.DoesNotContain("the audio bytes of Foo", text);
        // Local ready marker exists.
        Assert.True(h1.Ready.IsMarked("20260705-foo"));

        var bytesAfterCycle1 = File.ReadAllBytes(ws.ManifestPath);

        // ---- cycle 2: a second full WATCH → NORMALIZE over the same pair ----
        // Re-drive with a fresh worker but the SAME state store + manifest file, so
        // the idempotency ledger + recording dedupe + manifest id-dedupe all engage.
        var h2 = new WorkerHarness(ws, FolderId);
        // Share the durable state + manifest + blobs from the first run.
        var worker2 = new WatchWorker(h1.Config, h2.Drive, h1.State, // <- reuse h1.State
            new Downloader(h2.Drive, h1.Blobs, h1.Ledger,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Downloader>.Instance),
            h1.Normalizer, h1.Manifest, h1.Ready,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WatchWorker>.Instance);
        worker2.SetFolderId(FolderId);
        h2.Drive.SeedFile("A", "Foo.m4a", "audio/mp4", audioBytes,
            createdTime: DateTimeOffset.Parse("2026-07-05T09:30:00Z"), parents: new[] { FolderId });
        h2.Drive.SeedFile("T", "Foo.txt", "text/plain", txtBytes,
            createdTime: DateTimeOffset.Parse("2026-07-05T09:30:00Z"), parents: new[] { FolderId });
        h2.Drive.QueuePage(new[]
        {
            h2.Drive.Meta("A"),
            h2.Drive.Meta("T"),
        });

        await worker2.RunCycleAsync(default);

        var bytesAfterCycle2 = File.ReadAllBytes(ws.ManifestPath);
        Assert.Equal(bytesAfterCycle1, bytesAfterCycle2); // BYTE-unchanged no-op
    }

    [Fact]
    public async Task Lone_audio_produces_no_manifest_entry_until_transcript_arrives()
    {
        using var ws = new TempWorkspace();
        var h = new WorkerHarness(ws, FolderId);
        var audioBytes = System.Text.Encoding.UTF8.GetBytes("audio only");
        h.Drive.SeedFile("A", "Foo.m4a", "audio/mp4", audioBytes,
            createdTime: DateTimeOffset.Parse("2026-07-05T09:30:00Z"), parents: new[] { FolderId });
        // cycle 1: only the audio arrives.
        h.Drive.QueuePage(new[] { h.Drive.Meta("A") });
        await h.Worker.RunCycleAsync(default);

        // No manifest entry yet — the recording is pending.
        var text1 = File.Exists(ws.ManifestPath) ? File.ReadAllText(ws.ManifestPath) : "";
        Assert.DoesNotContain("- id:", text1);
        Assert.Equal(0, h.Worker.RecordingsNormalized);

        // cycle 2: the transcript arrives and completes the pair.
        h.Drive.SeedFile("T", "Foo.txt", "text/plain", System.Text.Encoding.UTF8.GetBytes("t"),
            createdTime: DateTimeOffset.Parse("2026-07-05T09:30:00Z"), parents: new[] { FolderId });
        h.Drive.QueuePage(new[] { h.Drive.Meta("T") });
        await h.Worker.RunCycleAsync(default);

        var text2 = File.ReadAllText(ws.ManifestPath);
        Assert.Contains("- id: 20260705-foo", text2);
        Assert.Equal(1, h.Worker.RecordingsNormalized);
    }

    // MIXED-cases (incremental path): a lone audio that stays single-sided across the grace window
    // (default 2 cycles) is FLUSHED as an audio-only recording — no longer silently held forever.
    [Fact]
    public async Task Incremental_lone_audio_flushes_as_audio_only_after_the_grace_window()
    {
        using var ws = new TempWorkspace();
        var h = new WorkerHarness(ws, FolderId); // default SingletonFlushGraceCycles = 2
        h.Drive.SeedFile("A", "Solo.m4a", "audio/mp4", System.Text.Encoding.UTF8.GetBytes("solo audio"),
            createdTime: DateTimeOffset.Parse("2026-07-05T09:30:00Z"), parents: new[] { FolderId });

        // Cycle 1: audio arrives; age = 1 (< grace 2) → held, not yet flushed.
        h.Drive.QueuePage(new[] { h.Drive.Meta("A") });
        await h.Worker.RunCycleAsync(default);
        Assert.Equal(0, h.Worker.RecordingsNormalized);

        // Cycle 2: no new files; age reaches 2 → flushed as an audio-only recording.
        h.Drive.QueuePage(Array.Empty<DriveChange>());
        await h.Worker.RunCycleAsync(default);
        Assert.Equal(1, h.Worker.RecordingsNormalized);
        var text = File.ReadAllText(ws.ManifestPath);
        Assert.Contains("- id: 20260705-solo", text);
        Assert.Contains("audio_sha256: " + BlobStore.Sha256Hex(System.Text.Encoding.UTF8.GetBytes("solo audio")), text);
    }

    // A lone transcript likewise flushes as a transcript-only recording (empty audio_sha256 in §8).
    [Fact]
    public async Task Incremental_lone_transcript_flushes_as_transcript_only_after_the_grace_window()
    {
        using var ws = new TempWorkspace();
        var h = new WorkerHarness(ws, FolderId);
        h.Drive.SeedFile("T", "Notes.txt", "text/plain", System.Text.Encoding.UTF8.GetBytes("just notes"),
            createdTime: DateTimeOffset.Parse("2026-07-05T09:30:00Z"), parents: new[] { FolderId });

        h.Drive.QueuePage(new[] { h.Drive.Meta("T") });
        await h.Worker.RunCycleAsync(default); // age 1
        Assert.Equal(0, h.Worker.RecordingsNormalized);

        h.Drive.QueuePage(Array.Empty<DriveChange>());
        await h.Worker.RunCycleAsync(default); // age 2 → flush
        Assert.Equal(1, h.Worker.RecordingsNormalized);
        var text = File.ReadAllText(ws.ManifestPath);
        Assert.Contains("- id: 20260705-notes", text);
        // Transcript-only: empty audio_sha256; the transcript's Drive id is the source_drive_id.
        Assert.Contains("audio_sha256: \n", text);
        Assert.Contains("source_drive_id: T", text);
    }
}
