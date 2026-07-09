using Cervello.Watcher;
using Cervello.Watcher.Domain;
using Cervello.Watcher.State;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cervello.Watcher.Tests;

/// <summary>
/// BACKFILL-SELF-HEAL — a re-backfill recovers cleanly from a PRIOR PARTIAL run: download-replays are
/// RE-OFFERED to the pairer (blobs reused from the ledger, no re-download), and a prior single-sided
/// recording row is UPGRADED to a pair (state reset to <c>normalized</c>) when its missing side is
/// re-offered. Converges + never duplicates on repeated runs. Reproduces + fixes the rc34 defects:
/// (1) replay-orphan — 144 rc33-downloaded files skipped + never paired; (2) mis-registered audio-only
/// singletons whose transcript was a replay, then failed enrichment (audio-only → transcribe).
/// </summary>
public sealed class SelfHealBackfillTests
{
    private const string FolderId = "folder-recordings";

    private static void SeedAudio(WorkerHarness h, string id, string basename, string body) =>
        h.Drive.SeedFile(id, basename + ".m4a", "audio/mp4",
            System.Text.Encoding.UTF8.GetBytes(body), parents: new[] { FolderId });

    private static void SeedTranscript(WorkerHarness h, string id, string basename, string body) =>
        h.Drive.SeedFile(id, basename + ".txt", "text/plain",
            System.Text.Encoding.UTF8.GetBytes(body), parents: new[] { FolderId });

    private static WatchWorker NewForced(WorkerHarness h)
    {
        var w = new WatchWorker(
            h.Config with { ForceBackfill = true },
            h.Drive, h.State, h.Downloader, h.Normalizer, h.Manifest, h.Ready,
            NullLogger<WatchWorker>.Instance);
        w.SetFolderId(FolderId);
        return w;
    }

    // (A) A download-REPLAY (already in the ledger, staged by a prior run) is RE-OFFERED to the pairer
    //     and pairs with a freshly-staged partner — WITHOUT re-downloading the replayed blob.
    [Fact]
    public async Task Replay_is_reoffered_and_pairs_without_redownload()
    {
        using var ws = new TempWorkspace();
        var h = new WorkerHarness(ws, FolderId);

        // Prior run (rc33-like): ONLY the audio existed → it was downloaded + staged (ledger recorded),
        // and flushed as an audio-only singleton.
        SeedAudio(h, "A1", "Rec1", "audio-bytes-1");
        await h.Worker.BackfillIfNeededAsync(default);
        Assert.Equal(1, h.Worker.RecordingsNormalized);           // 1 audio-only recording
        var audioDownloadsAfterFirst = h.Drive.DownloadCallCount; // audio fetched exactly once

        // Now the transcript partner appears. Re-backfill: the audio is a LEDGER REPLAY (must not
        // re-download) and the transcript is freshly staged → they PAIR.
        SeedTranscript(h, "T1", "Rec1", "transcript-bytes-1");
        var forced = NewForced(h);
        await forced.BackfillIfNeededAsync(default);

        // The replayed audio blob was NOT re-downloaded (only the new transcript was fetched).
        Assert.Equal(audioDownloadsAfterFirst + 1, h.Drive.DownloadCallCount);
        Assert.Contains("T1", h.Drive.DownloadedFileIds);
        Assert.Single(h.Drive.DownloadedFileIds, "A1"); // audio fetched exactly once, ever

        // The recording is now a PAIR: one row for the id, carrying both sides.
        var id = Normalize.NormalizerId("Rec1", "A1", h);
        var rows = await h.State.GetRecordingsByIdAsync(id, default);
        var row = Assert.Single(rows);
        Assert.False(string.IsNullOrEmpty(row.AudioSha256));  // audio side present
        Assert.NotNull(row.TranscriptSha256);                 // transcript side now present
        Assert.NotNull(row.TxtDriveId);
    }

    // (B) A prior AUDIO-ONLY singleton row is UPGRADED to a pair (state reset to normalized) when its
    //     transcript is re-offered; and a matching-both-sides re-register stays a no-op.
    [Fact]
    public async Task Audio_only_singleton_is_upgraded_to_a_pair_and_reregister_is_noop()
    {
        using var ws = new TempWorkspace();
        var h = new WorkerHarness(ws, FolderId);

        SeedAudio(h, "A1", "Rec1", "audio-bytes-1");
        await h.Worker.BackfillIfNeededAsync(default);

        var id = Normalize.NormalizerId("Rec1", "A1", h);
        var before = Assert.Single(await h.State.GetRecordingsByIdAsync(id, default));
        Assert.True(string.IsNullOrEmpty(before.TranscriptSha256 ?? "")); // audio-only

        // Transcript arrives → re-backfill upgrades the row.
        SeedTranscript(h, "T1", "Rec1", "transcript-bytes-1");
        var forced = NewForced(h);
        await forced.BackfillIfNeededAsync(default);

        Assert.Equal(1, forced.RecordingsUpgraded);            // one single-sided → pair upgrade
        var after = Assert.Single(await h.State.GetRecordingsByIdAsync(id, default));
        Assert.NotNull(after.TranscriptSha256);               // side filled in
        Assert.Equal(PipelineState.Normalized, after.State);  // state reset so the drain reprocesses

        // A THIRD backfill over the fully-correct pair is a pure no-op (no further upgrade/duplicate).
        var again = NewForced(h);
        await again.BackfillIfNeededAsync(default);
        Assert.Equal(0, again.RecordingsUpgraded);
        Assert.Equal(0, again.RecordingsNormalized);
        Assert.Single(await h.State.GetRecordingsByIdAsync(id, default)); // still exactly one row
    }

    // (B') A transcript-only singleton is upgraded to a pair when its audio is re-offered (the mirror
    //      direction), and the row's key migrates from the txt: family to the audio-sha family.
    [Fact]
    public async Task Transcript_only_singleton_is_upgraded_when_audio_arrives()
    {
        using var ws = new TempWorkspace();
        var h = new WorkerHarness(ws, FolderId);

        SeedTranscript(h, "T1", "Rec1", "transcript-bytes-1");
        await h.Worker.BackfillIfNeededAsync(default);
        var id = Normalize.NormalizerId("Rec1", "T1", h);
        var before = Assert.Single(await h.State.GetRecordingsByIdAsync(id, default));
        Assert.True(string.IsNullOrEmpty(before.AudioSha256)); // transcript-only

        SeedAudio(h, "A1", "Rec1", "audio-bytes-1");
        var forced = NewForced(h);
        await forced.BackfillIfNeededAsync(default);

        Assert.Equal(1, forced.RecordingsUpgraded);
        var after = Assert.Single(await h.State.GetRecordingsByIdAsync(id, default)); // exactly one row
        Assert.False(string.IsNullOrEmpty(after.AudioSha256)); // audio side filled
        Assert.NotNull(after.TranscriptSha256);
        Assert.Equal(PipelineState.Normalized, after.State);
    }

    // Convergence: a mix of REPLAYS (from a prior partial run) + NEW files re-backfills to every
    // recording registered EXACTLY once with correct sides — the rc34 end-state, self-healed.
    [Fact]
    public async Task Mixed_replays_and_new_files_converge_to_correct_sides_once()
    {
        using var ws = new TempWorkspace();
        var h = new WorkerHarness(ws, FolderId);

        // Prior partial run: two audio files + one transcript existed (the transcript for Rec1's
        // partner was missing then). All get staged; Rec1/Rec2 land as audio-only singletons, Rec3
        // as transcript-only.
        SeedAudio(h, "A1", "Rec1", "a1");
        SeedAudio(h, "A2", "Rec2", "a2");
        SeedTranscript(h, "T3", "Rec3", "t3");
        await h.Worker.BackfillIfNeededAsync(default);
        Assert.Equal(3, h.Worker.RecordingsNormalized); // 3 singletons
        var downloadsAfterFirst = h.Drive.DownloadCallCount;

        // The missing partners now appear: Rec1's transcript, Rec2's transcript, Rec3's audio, plus a
        // brand-new complete pair Rec4.
        SeedTranscript(h, "T1", "Rec1", "t1");
        SeedTranscript(h, "T2", "Rec2", "t2");
        SeedAudio(h, "A3", "Rec3", "a3");
        SeedAudio(h, "A4", "Rec4", "a4");
        SeedTranscript(h, "T4", "Rec4", "t4");

        var forced = NewForced(h);
        await forced.BackfillIfNeededAsync(default);

        // Only the FIVE genuinely-new blobs were downloaded (T1, T2, A3 partners + the brand-new Rec4
        // pair A4+T4); the three PRIOR blobs (A1, A2, T3) replayed from the ledger with NO re-download.
        Assert.True(downloadsAfterFirst + 5 == h.Drive.DownloadCallCount,
            "unexpected re-download; DLIDS=" + string.Join(",", h.Drive.DownloadedFileIds));
        Assert.DoesNotContain(h.Drive.DownloadedFileIds.Skip(downloadsAfterFirst), id => id is "A1" or "A2" or "T3");

        // Every recording is now exactly one row with BOTH sides.
        foreach (var (basename, audioId) in new[] { ("Rec1", "A1"), ("Rec2", "A2"), ("Rec3", "A3"), ("Rec4", "A4") })
        {
            var id = Normalize.NormalizerId(basename, audioId, h);
            var row = Assert.Single(await h.State.GetRecordingsByIdAsync(id, default));
            Assert.False(string.IsNullOrEmpty(row.AudioSha256));
            Assert.NotNull(row.TranscriptSha256);
        }

        // 3 upgrades (Rec1, Rec2, Rec3) + 1 fresh pair (Rec4).
        Assert.Equal(3, forced.RecordingsUpgraded);
        Assert.Equal(1, forced.RecordingsNormalized);

        // A further re-backfill is a total no-op (convergence is stable).
        var again = NewForced(h);
        await again.BackfillIfNeededAsync(default);
        Assert.Equal(0, again.RecordingsUpgraded);
        Assert.Equal(0, again.RecordingsNormalized);
    }
}

/// <summary>Test-local helper to compute the deterministic recording id the Normalizer assigns, so a
/// test can look up the registered row by id without duplicating the slug/date logic.</summary>
internal static class Normalize
{
    public static string NormalizerId(string basename, string anchorFileId, WorkerHarness h)
    {
        var change = h.Drive.Meta(anchorFileId);
        var when = change.CreatedTime ?? change.ModifiedTime!.Value;
        return $"{when.UtcDateTime:yyyyMMdd}-{Slug(basename)}";
    }

    private static string Slug(string input)
    {
        var sb = new System.Text.StringBuilder(input.Length);
        var lastHyphen = false;
        foreach (var ch in input.ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') { sb.Append(ch); lastHyphen = false; }
            else if (!lastHyphen && sb.Length > 0) { sb.Append('-'); lastHyphen = true; }
        }
        var slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "recording" : slug;
    }
}
