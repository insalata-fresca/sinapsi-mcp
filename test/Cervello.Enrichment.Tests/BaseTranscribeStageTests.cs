using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Pipeline.Stages;
using Cervello.Enrichment.Ports;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// text-correction — "Base transcript is the correction substrate" / "Base transcript persisted
/// before correction". The engine re-transcribes via CT126 (faked) and persists the base at
/// recordings/transcripts/&lt;id&gt;.md; the base is written once and never overwritten.
/// </summary>
public sealed class BaseTranscribeStageTests
{
    private static RecordingRef Rec() => new("20260601-guilhem", "sha-aaa", "m4a", "fr", ready: true);
    private static ReadOnlyMemory<byte> Audio() => new byte[] { 1, 2, 3, 4 };

    // ---- Scenario: Base transcript persisted before correction ----
    [Fact]
    public async Task Base_transcript_is_transcribed_and_persisted_at_the_section8_path()
    {
        var transcribe = new FakeTranscribeClient(new BaseTranscript("# Guilhem 1-1\n\nbonjour…", "fr"));
        var store = new InMemoryTranscriptStore();
        var stage = new BaseTranscribeStage(transcribe, store);

        var result = await stage.TranscribeAsync(Rec(), Audio());

        Assert.Equal("recordings/transcripts/20260601-guilhem.md", result.TranscriptPath);
        Assert.False(result.AlreadyExisted);
        Assert.Equal(1, transcribe.Calls);
        // Persisted, in the correct language.
        var persisted = store.Read("20260601-guilhem");
        Assert.NotNull(persisted);
        Assert.Equal("fr", persisted!.Language);
        Assert.Equal(("m4a", "fr", 4), transcribe.Seen.Single());
    }

    // ---- The base is written once — a second run is a no-op and never overwrites ----
    [Fact]
    public async Task Base_transcript_is_not_re_transcribed_or_overwritten_on_a_second_run()
    {
        var transcribe = new FakeTranscribeClient(new BaseTranscript("first", "fr"));
        var store = new InMemoryTranscriptStore();
        var stage = new BaseTranscribeStage(transcribe, store);

        await stage.TranscribeAsync(Rec(), Audio());
        var second = await stage.TranscribeAsync(Rec(), Audio());

        Assert.True(second.AlreadyExisted);
        Assert.Equal(1, transcribe.Calls); // NOT re-transcribed
        Assert.Equal("first", store.Read("20260601-guilhem")!.Markdown); // unchanged
    }

    // ---- The in-memory store models the immutability guard: a direct overwrite throws ----
    [Fact]
    public async Task Store_refuses_to_overwrite_an_existing_base()
    {
        var store = new InMemoryTranscriptStore();
        await store.WriteBaseAsync("id", new BaseTranscript("a", "fr"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.WriteBaseAsync("id", new BaseTranscript("b", "fr")));
    }
}
