using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Pipeline;
using Cervello.Enrichment.Ports;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// V4 orchestration tests (design <c>ste/cervello</c> <c>docs/design/voiceprint-naming.md</c> §7
/// phase V4): cluster (V1, real) → pick window (V2 picker, real) → resolve audio → cut clip → upload
/// → persist candidate, against FAKE seams (no ffmpeg, no gateway, no Postgres). Exercises the
/// never-fabricate skip paths and the regenerate-replaces-unresolved-only contract.
///
/// SYNTHETIC 192-d vectors + synthetic bytes only — no personal audio, no real biometric vector.
/// </summary>
public sealed class VoiceSampleGeneratorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeAudioClipCutter(bool fail = false) : IAudioClipCutter
    {
        public int Cuts { get; private set; }

        public Task<AudioClip> CutClipAsync(
            ReadOnlyMemory<byte> sourceAudio, string audioFormat, RepresentativeWindow window, CancellationToken ct = default)
        {
            Cuts++;
            if (fail)
                throw new AudioClipCutFailedException("synthetic cut failure");
            return Task.FromResult(new AudioClip(new byte[] { 1, 2, 3 }, "m4a"));
        }
    }

    private sealed class FakeVoiceSampleUploader(bool failUpload = false) : IVoiceSampleUploader
    {
        private int _next = 1;
        public List<string> Uploaded { get; } = [];
        public List<string> Deleted { get; } = [];

        public Task<string> UploadAsync(string fileName, ReadOnlyMemory<byte> clipBytes, string mimeType, CancellationToken ct = default)
        {
            if (failUpload)
                throw new InvalidOperationException("synthetic upload failure");
            var id = $"drive-file-{_next++}";
            Uploaded.Add(id);
            return Task.FromResult(id);
        }

        public Task DeleteAsync(string driveFileId, CancellationToken ct = default)
        {
            Deleted.Add(driveFileId);
            return Task.CompletedTask;
        }
    }

    /// <summary>Build a corpus store with one voice: two clusters (same synthetic centroid) each with segments.</summary>
    private static async Task<(InMemoryRecordingVoiceprintStore Corpus, InMemoryRecordingAudioRefResolver Audio)> SeedOneVoiceAsync()
    {
        var corpus = new InMemoryRecordingVoiceprintStore();
        var segs = new List<DiarizedSegment> { new("s1", 0.0, 20.0) }; // 20s run — above the 1s min window
        await corpus.PersistAsync("rec-1", [
            new RecordingVoiceprint("rec-1", 0, TestVectors.Axis(0), "spkrec-ecapa-voxceleb", 1, 20.0, "s1", T0, segs),
        ]);

        var audio = new InMemoryRecordingAudioRefResolver()
            .Add(new RecordingAudioRef("rec-1", "sha-abc123", "m4a"));

        return (corpus, audio);
    }

    private static VoiceSampleGenerator BuildGenerator(
        InMemoryRecordingVoiceprintStore corpus,
        InMemoryRecordingAudioRefResolver audioRefs,
        FakeAudioSource? audioSource = null,
        FakeAudioClipCutter? cutter = null,
        FakeVoiceSampleUploader? uploader = null,
        InMemoryVoiceprintNamingCandidateStore? store = null)
    {
        var clusterer = new VoiceReviewClusterer(corpus, enrolledStore: null);
        return new VoiceSampleGenerator(
            clusterer, corpus, audioRefs,
            audioSource ?? new FakeAudioSource(),
            cutter ?? new FakeAudioClipCutter(),
            uploader ?? new FakeVoiceSampleUploader(),
            store ?? new InMemoryVoiceprintNamingCandidateStore());
    }

    [Fact] // scenario: one clean voice end-to-end -> one uploaded candidate, no skips
    public async Task Generates_one_candidate_for_a_clean_voice()
    {
        var (corpus, audioRefs) = await SeedOneVoiceAsync();
        var uploader = new FakeVoiceSampleUploader();
        var store = new InMemoryVoiceprintNamingCandidateStore();
        var gen = BuildGenerator(corpus, audioRefs, uploader: uploader, store: store);

        var result = await gen.GenerateAsync(maxCandidates: 15);

        Assert.Single(result.Uploaded);
        Assert.Empty(result.Skipped);
        Assert.Equal("unknown_01", result.Uploaded[0].SampleName);
        Assert.Equal("drive-file-1", result.Uploaded[0].DriveFileId);
        Assert.Equal(TestVectors.Axis(0), result.Uploaded[0].Centroid);

        var persisted = await store.GetUnresolvedAsync();
        Assert.Single(persisted);
        Assert.Equal("drive-file-1", persisted[0].DriveFileId);
    }

    [Fact] // scenario: sample name is unknown_NN by coverage rank, 1-indexed
    public async Task Sample_names_are_1_indexed_by_coverage_rank()
    {
        var corpus = new InMemoryRecordingVoiceprintStore();
        // Voice A: more total duration -> ranked first (unknown_01). Voice B: less -> unknown_02.
        await corpus.PersistAsync("rec-a", [
            new RecordingVoiceprint("rec-a", 0, TestVectors.Axis(0), "m", 1, 30.0, "s1", T0,
                [new DiarizedSegment("s1", 0, 30)]),
        ]);
        await corpus.PersistAsync("rec-b", [
            new RecordingVoiceprint("rec-b", 0, TestVectors.Axis(50), "m", 1, 5.0, "s1", T0,
                [new DiarizedSegment("s1", 0, 5)]),
        ]);
        var audioRefs = new InMemoryRecordingAudioRefResolver()
            .Add(new RecordingAudioRef("rec-a", "sha-a", "m4a"))
            .Add(new RecordingAudioRef("rec-b", "sha-b", "m4a"));

        var gen = BuildGenerator(corpus, audioRefs);
        var result = await gen.GenerateAsync(maxCandidates: 15);

        Assert.Equal(2, result.Uploaded.Count);
        Assert.Equal("unknown_01", result.Uploaded[0].SampleName);
        Assert.Equal("unknown_02", result.Uploaded[1].SampleName);
    }

    [Fact] // scenario: no persisted segment ranges -> skip, never fabricate a window
    public async Task Skips_a_voice_with_no_persisted_segments()
    {
        var corpus = new InMemoryRecordingVoiceprintStore();
        await corpus.PersistAsync("rec-1", [
            new RecordingVoiceprint("rec-1", 0, TestVectors.Axis(0), "m", 1, 20.0, "s1", T0, segments: null), // no ranges
        ]);
        var audioRefs = new InMemoryRecordingAudioRefResolver().Add(new RecordingAudioRef("rec-1", "sha", "m4a"));

        var gen = BuildGenerator(corpus, audioRefs);
        var result = await gen.GenerateAsync(maxCandidates: 15);

        Assert.Empty(result.Uploaded);
        Assert.Single(result.Skipped);
        Assert.Contains("no representative window", result.Skipped[0].Reason);
    }

    [Fact] // scenario: audio ref cannot be resolved (unknown recording) -> skip, never fabricate
    public async Task Skips_a_voice_whose_audio_ref_cannot_be_resolved()
    {
        var (corpus, _) = await SeedOneVoiceAsync();
        var emptyAudioRefs = new InMemoryRecordingAudioRefResolver(); // rec-1 NOT added

        var gen = BuildGenerator(corpus, emptyAudioRefs);
        var result = await gen.GenerateAsync(maxCandidates: 15);

        Assert.Empty(result.Uploaded);
        Assert.Single(result.Skipped);
        Assert.Contains("could not be resolved", result.Skipped[0].Reason);
    }

    [Fact] // scenario: IAudioSource throws AudioUnavailableException -> skip, never fabricate
    public async Task Skips_a_voice_whose_audio_blob_is_unavailable()
    {
        var (corpus, audioRefs) = await SeedOneVoiceAsync();
        var gen = BuildGenerator(corpus, audioRefs, audioSource: new FakeAudioSource(unavailable: true));

        var result = await gen.GenerateAsync(maxCandidates: 15);

        Assert.Empty(result.Uploaded);
        Assert.Single(result.Skipped);
        Assert.Contains("audio unavailable", result.Skipped[0].Reason);
    }

    [Fact] // scenario: ffmpeg cut fails -> skip, never fabricate a clip
    public async Task Skips_a_voice_whose_clip_cut_fails()
    {
        var (corpus, audioRefs) = await SeedOneVoiceAsync();
        var gen = BuildGenerator(corpus, audioRefs, cutter: new FakeAudioClipCutter(fail: true));

        var result = await gen.GenerateAsync(maxCandidates: 15);

        Assert.Empty(result.Uploaded);
        Assert.Single(result.Skipped);
        Assert.Contains("clip cut failed", result.Skipped[0].Reason);
    }

    [Fact] // scenario: Drive upload fails -> skip, never fabricate a file id
    public async Task Skips_a_voice_whose_upload_fails()
    {
        var (corpus, audioRefs) = await SeedOneVoiceAsync();
        var gen = BuildGenerator(corpus, audioRefs, uploader: new FakeVoiceSampleUploader(failUpload: true));

        var result = await gen.GenerateAsync(maxCandidates: 15);

        Assert.Empty(result.Uploaded);
        Assert.Single(result.Skipped);
        Assert.Contains("Drive upload failed", result.Skipped[0].Reason);
    }

    [Fact] // scenario: re-running regenerates the unresolved set and clears the stale Drive clip
    public async Task Regenerate_replaces_prior_unresolved_candidates_and_deletes_their_clips()
    {
        var (corpus, audioRefs) = await SeedOneVoiceAsync();
        var uploader = new FakeVoiceSampleUploader();
        var store = new InMemoryVoiceprintNamingCandidateStore();
        var gen = BuildGenerator(corpus, audioRefs, uploader: uploader, store: store);

        var first = await gen.GenerateAsync(maxCandidates: 15);
        Assert.Single(first.Uploaded);
        var firstFileId = first.Uploaded[0].DriveFileId;

        var second = await gen.GenerateAsync(maxCandidates: 15);

        Assert.Single(second.Uploaded);
        Assert.NotEqual(firstFileId, second.Uploaded[0].DriveFileId); // fresh upload, new file id
        Assert.Contains(firstFileId, second.DeletedStaleDriveFileIds);
        Assert.Contains(firstFileId, uploader.Deleted); // the stale Drive clip was cleaned up

        var unresolved = await store.GetUnresolvedAsync();
        Assert.Single(unresolved); // never duplicates
    }

    [Fact] // scenario: a RESOLVED candidate is never touched by a regenerate
    public async Task Regenerate_never_touches_a_resolved_candidate()
    {
        var (corpus, audioRefs) = await SeedOneVoiceAsync();
        var store = new InMemoryVoiceprintNamingCandidateStore();
        var uploader = new FakeVoiceSampleUploader();
        var gen = BuildGenerator(corpus, audioRefs, uploader: uploader, store: store);

        var first = await gen.GenerateAsync(maxCandidates: 15);
        var resolvedFileId = first.Uploaded[0].DriveFileId;
        Assert.True(await store.MarkResolvedAsync(resolvedFileId));

        var second = await gen.GenerateAsync(maxCandidates: 15);

        Assert.DoesNotContain(resolvedFileId, second.DeletedStaleDriveFileIds);
        Assert.DoesNotContain(resolvedFileId, uploader.Deleted);
        var resolved = await store.GetByDriveFileIdAsync(resolvedFileId);
        Assert.NotNull(resolved);
        Assert.True(resolved!.Resolved);
    }

    [Fact] // scenario: an unexpected exception for one voice never aborts the whole batch
    public async Task One_voice_failure_never_blocks_other_voices()
    {
        var corpus = new InMemoryRecordingVoiceprintStore();
        await corpus.PersistAsync("rec-a", [
            new RecordingVoiceprint("rec-a", 0, TestVectors.Axis(0), "m", 1, 30.0, "s1", T0,
                [new DiarizedSegment("s1", 0, 30)]),
        ]);
        await corpus.PersistAsync("rec-b", [
            new RecordingVoiceprint("rec-b", 0, TestVectors.Axis(50), "m", 1, 20.0, "s1", T0,
                [new DiarizedSegment("s1", 0, 20)]),
        ]);
        // Only rec-b's audio resolves — rec-a (ranked first, more duration) is skipped, rec-b still succeeds.
        var audioRefs = new InMemoryRecordingAudioRefResolver()
            .Add(new RecordingAudioRef("rec-b", "sha-b", "m4a"));

        var gen = BuildGenerator(corpus, audioRefs);
        var result = await gen.GenerateAsync(maxCandidates: 15);

        Assert.Single(result.Uploaded);
        Assert.Single(result.Skipped);
        Assert.Equal("unknown_01", result.Skipped[0].SampleName); // rec-a, ranked first, skipped
        Assert.Equal("unknown_02", result.Uploaded[0].SampleName); // rec-b still generated
    }

    [Fact]
    public async Task Rejects_a_non_positive_maxCandidates()
    {
        var corpus = new InMemoryRecordingVoiceprintStore();
        var gen = BuildGenerator(corpus, new InMemoryRecordingAudioRefResolver());
        await Assert.ThrowsAsync<ArgumentException>(() => gen.GenerateAsync(0));
    }
}
