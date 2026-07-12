using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Pipeline;
using Cervello.Enrichment.Ports;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// The registry-pilot enrol coordinator (<see cref="RegistryClipEnroller"/>): it seeds the enrolled
/// voiceprint store from the operator's named clips in Drive <c>registry/</c>, reusing the REAL guards
/// (§10 gate + consent + <see cref="VoiceprintEnrollment"/>). SYNTHETIC 256-d vectors only — no personal
/// audio, no real biometric vector ever appears here.
/// </summary>
public sealed class RegistryClipEnrollerTests
{
    private static readonly DateOnly Day1 = new(2026, 7, 12);

    private static DiarizeEmbedResponse OneSpeaker(int axis) => new(
        segments: [new DiarizedSegment("s1", 0, 20)],
        embeddings: [new SpeakerEmbedding("s1", TestVectors.Axis(axis))],
        model: new DiarizeEmbedModel("silero-vad", "pyannote/wespeaker-voxceleb-resnet34-LM", 256));

    [Fact] // scenario: a named registry clip enrolls the slug via the REAL VoiceprintEnrollment path
    public async Task Registry_clip_enrolls_slug_through_the_real_enrollment_path()
    {
        var reader = new FakeRegistryClipReader()
            .With("file-1", "013_stefano-ursino", audio: [1, 2, 3]);
        var store = new InMemoryVoiceprintStore(new EnrollmentAllowlist([]), new InMemoryEnrollmentConsentStore());
        var enroller = Enroller(reader, FakeDiarizeEmbedClient.Returning(OneSpeaker(0)), store, out var consent);

        var result = await enroller.EnrollAsync(["stefano-ursino"], Day1);

        Assert.Single(result.Enrolled);
        Assert.Empty(result.Skipped);
        Assert.False(result.Enrolled[0].WasRefine);
        Assert.Equal("013_stefano-ursino", result.Enrolled[0].ClipName);
        var print = await store.GetAsync("stefano-ursino");
        Assert.NotNull(print);
        Assert.Equal(1, print!.SampleCount);
        Assert.Equal(["registry://file-1"], print.SourceSegments); // provenance, no centroid leaked
        // The §10 gate passed ONLY because the enroller recorded consent (static allowlist was empty).
        Assert.True(await consent.IsConsentedAsync("stefano-ursino"));
    }

    [Fact] // scenario: consent carries the human://registry-pilot:<slug> basis (operator naming = consent)
    public async Task Enrollment_records_registry_pilot_human_basis()
    {
        var reader = new FakeRegistryClipReader().With("f", "guilhem-dupuy_01", audio: [9]);
        var consent = new RecordingConsentStore();
        var store = new InMemoryVoiceprintStore(new EnrollmentAllowlist([]), consent);
        var enroller = new RegistryClipEnroller(reader, FakeDiarizeEmbedClient.Returning(OneSpeaker(1)), consent,
            new VoiceprintEnrollment(store));

        await enroller.EnrollAsync(["guilhem-dupuy"], Day1);

        Assert.Equal("human://registry-pilot:guilhem-dupuy", consent.BasisFor("guilhem-dupuy"));
    }

    [Fact] // scenario: multiple clips for one person REFINE the running centroid (sample_count climbs)
    public async Task Multiple_clips_for_one_person_refine()
    {
        var reader = new FakeRegistryClipReader()
            .With("a", "marwin-henry_02", audio: [1])
            .With("b", "marwin-henry_06", audio: [2]);
        var store = new InMemoryVoiceprintStore(new EnrollmentAllowlist([]), new InMemoryEnrollmentConsentStore());
        var enroller = Enroller(reader, FakeDiarizeEmbedClient.Returning(OneSpeaker(3)), store, out _);

        var result = await enroller.EnrollAsync(["marwin-henry"], Day1);

        Assert.Equal(2, result.Enrolled.Count);
        Assert.False(result.Enrolled[0].WasRefine);
        Assert.True(result.Enrolled[1].WasRefine);
        Assert.Equal(2, (await store.GetAsync("marwin-henry"))!.SampleCount);
    }

    [Fact] // scenario: the DOMINANT speaker's embedding is the one enrolled (robust to a short interjection)
    public async Task Dominant_speaker_embedding_is_enrolled()
    {
        // s1 talks 18s (axis 5), s2 interjects 2s (axis 40). The enrolled centroid must be s1's.
        var response = new DiarizeEmbedResponse(
            segments: [new DiarizedSegment("s1", 0, 18), new DiarizedSegment("s2", 18, 20)],
            embeddings: [new SpeakerEmbedding("s1", TestVectors.Axis(5)), new SpeakerEmbedding("s2", TestVectors.Axis(40))],
            model: new DiarizeEmbedModel("silero-vad", "pyannote/wespeaker-voxceleb-resnet34-LM", 256));
        var reader = new FakeRegistryClipReader().With("f", "mikael", audio: [7]);
        var store = new InMemoryVoiceprintStore(new EnrollmentAllowlist([]), new InMemoryEnrollmentConsentStore());
        var enroller = Enroller(reader, FakeDiarizeEmbedClient.Returning(response), store, out _);

        await enroller.EnrollAsync(["mikael"], Day1);

        var matchS1 = await store.MatchAsync(TestVectors.Axis(5));
        Assert.Equal("mikael", matchS1[0].PersonSlug);
        Assert.True(matchS1[0].Cosine > 0.99); // enrolled centroid == s1's axis-5 vector, not s2's
    }

    [Fact] // scenario: a slug with no matching registry clip is SKIPPED with a reason, never fabricated
    public async Task Slug_with_no_matching_clip_is_skipped()
    {
        var reader = new FakeRegistryClipReader().With("f", "013_stefano-ursino", audio: [1]);
        var store = new InMemoryVoiceprintStore(new EnrollmentAllowlist([]), new InMemoryEnrollmentConsentStore());
        var enroller = Enroller(reader, FakeDiarizeEmbedClient.Returning(OneSpeaker(0)), store, out _);

        var result = await enroller.EnrollAsync(["marwin-henry"], Day1);

        Assert.Empty(result.Enrolled);
        Assert.Single(result.Skipped);
        Assert.Equal("no matching registry clip", result.Skipped[0].Reason);
        Assert.Null(await store.GetAsync("marwin-henry"));
    }

    private static RegistryClipEnroller Enroller(
        IVoiceprintRegistryClipReader reader, IDiarizeEmbedClient embed, InMemoryVoiceprintStore store,
        out IEnrollmentConsentStore consent)
    {
        var c = new RecordingConsentStore();
        consent = c;
        // The store must share the SAME consent instance so its §10 gate sees the enroller's AddConsent.
        return new RegistryClipEnroller(reader, embed, c, new VoiceprintEnrollment(store));
    }

    // ── test doubles ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>An in-memory registry-clip reader: canned {fileId → name} listing + per-file bytes.</summary>
    private sealed class FakeRegistryClipReader : IVoiceprintRegistryClipReader
    {
        private readonly List<DriveFileEntry> _files = [];
        private readonly Dictionary<string, byte[]> _bytes = new(StringComparer.Ordinal);

        public FakeRegistryClipReader With(string fileId, string name, byte[] audio)
        {
            _files.Add(new DriveFileEntry(fileId, name));
            _bytes[fileId] = audio;
            return this;
        }

        public Task<IReadOnlyList<DriveFileEntry>> ListRegistryFolderAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DriveFileEntry>>(_files);

        public Task<ReadOnlyMemory<byte>> DownloadClipAsync(string fileId, CancellationToken ct = default) =>
            Task.FromResult<ReadOnlyMemory<byte>>(_bytes[fileId]);
    }

    /// <summary>A consent store the store shares, so a test can assert the recorded basis.</summary>
    private sealed class RecordingConsentStore : IEnrollmentConsentStore
    {
        private readonly Dictionary<string, string> _consented = new(StringComparer.Ordinal);

        public Task<bool> AddConsentAsync(string personSlug, string basisId, CancellationToken ct = default)
        {
            var added = !_consented.ContainsKey(personSlug);
            _consented[personSlug] = basisId;
            return Task.FromResult(added);
        }

        public Task<bool> IsConsentedAsync(string personSlug, CancellationToken ct = default) =>
            Task.FromResult(_consented.ContainsKey(personSlug));

        public string? BasisFor(string personSlug) => _consented.TryGetValue(personSlug, out var b) ? b : null;
    }
}
