using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Pipeline.Stages;
using Cervello.Enrichment.Ports;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// diarize-embed-sidecar — the request/response contract as implemented on the engine side,
/// exercised through the <see cref="IDiarizeEmbedClient"/> seam via a deterministic fake (no
/// live endpoint, no network, no personal audio). These assert the contract E2a's server must
/// satisfy: 192-d L2-normalised embeddings, one per speaker; the model identity block; the
/// failure classification onto SCHEMAS §5 states.
/// </summary>
public sealed class DiarizeEmbedContractTests
{
    private static readonly DiarizeEmbedModel V1Model =
        new(vad: "silero-vad", embed: "speechbrain/spkrec-ecapa-voxceleb", dim: 192);

    private static RecordingRef Rec() => new("20260601-guilhem", "abc123sha", "m4a", "fr", ready: true);

    private static ReadOnlyMemory<byte> Audio() => new byte[] { 0x00, 0x01, 0x02, 0x03 };

    private static DiarizeEmbedResponse TwoSpeakerResponse() => new(
        segments:
        [
            new DiarizedSegment("s1", 0.0, 4.2),
            new DiarizedSegment("s2", 4.2, 9.0),
            new DiarizedSegment("s1", 9.0, 12.0),
        ],
        embeddings:
        [
            new SpeakerEmbedding("s1", TestVectors.Axis(0)),
            new SpeakerEmbedding("s2", TestVectors.Axis(50)),
        ],
        model: V1Model);

    // ---- Scenario: Diarize + embed a recording ----
    [Fact]
    public async Task Diarize_embed_returns_segments_and_one_192d_embedding_per_speaker()
    {
        var fake = FakeDiarizeEmbedClient.Returning(TwoSpeakerResponse());
        var stage = new DiarizeEmbedStage(fake);

        var result = await stage.DiarizeEmbedAsync(Rec(), Audio());

        Assert.True(result.Succeeded);
        // One cluster per distinct speaker, each with a 192-d vector and its own segments.
        Assert.Equal(2, result.Clusters.Count);
        Assert.All(result.Clusters, c => Assert.Equal(192, c.Centroid.Count));
        var s1 = result.Clusters.Single(c => c.Speaker == "s1");
        Assert.Equal(2, s1.Segments.Count); // s1 speaks twice
        Assert.Equal(1, fake.Calls);
    }

    // ---- Scenario: Model identity is returned ----
    [Fact]
    public async Task Model_identity_block_is_the_v1_ungated_stack()
    {
        var stage = new DiarizeEmbedStage(FakeDiarizeEmbedClient.Returning(TwoSpeakerResponse()));
        var result = await stage.DiarizeEmbedAsync(Rec(), Audio());

        Assert.NotNull(result.Model);
        Assert.Equal("silero-vad", result.Model!.Vad);
        Assert.Equal("speechbrain/spkrec-ecapa-voxceleb", result.Model.Embed);
        Assert.Equal(192, result.Model.Dim);
    }

    // ---- Contract invariant: an embedding vector that is not 192-d is rejected at the boundary ----
    [Fact]
    public void An_embedding_that_is_not_192d_is_rejected_by_the_contract_type()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SpeakerEmbedding("s1", new float[128]));
        Assert.Contains("192", ex.Message);
    }

    // ---- Scenario: Client is a swappable seam ----
    [Fact]
    public void The_stage_depends_only_on_the_port_not_a_concrete_http_or_pyannote_client()
    {
        // The stage's only diarize dependency is the IDiarizeEmbedClient interface.
        var ctor = typeof(DiarizeEmbedStage).GetConstructors().Single();
        Assert.Equal(typeof(IDiarizeEmbedClient), ctor.GetParameters()[0].ParameterType);
    }

    // ---- Scenario: Transient sidecar error retries (→ failed_retryable) ----
    [Fact]
    public async Task Transient_sidecar_error_maps_to_failed_retryable()
    {
        var fake = FakeDiarizeEmbedClient.Faulting(new DiarizeEmbedTransientException("gateway timeout"));
        var stage = new DiarizeEmbedStage(fake);

        var result = await stage.DiarizeEmbedAsync(Rec(), Audio());

        Assert.False(result.Succeeded);
        Assert.Equal(EnrichmentState.FailedRetryable, result.FailureState);
        Assert.Empty(result.Clusters); // no fabricated segments/embeddings
        Assert.Contains("timeout", result.Reason);
    }

    // ---- Scenario: Undecodable audio is terminal (→ failed_terminal, nothing invented) ----
    [Fact]
    public async Task Terminal_sidecar_error_maps_to_failed_terminal_and_invents_nothing()
    {
        var fake = FakeDiarizeEmbedClient.Faulting(new DiarizeEmbedTerminalException("invalid-audio"));
        var stage = new DiarizeEmbedStage(fake);

        var result = await stage.DiarizeEmbedAsync(Rec(), Audio());

        Assert.False(result.Succeeded);
        Assert.Equal(EnrichmentState.FailedTerminal, result.FailureState);
        Assert.Empty(result.Clusters);
        Assert.Equal("invalid-audio", result.Reason);
    }

    // ---- Confinement analogue: the client retains no audio after the call ----
    [Fact]
    public async Task Client_retains_no_audio_after_the_call()
    {
        var fake = FakeDiarizeEmbedClient.Returning(TwoSpeakerResponse());
        var stage = new DiarizeEmbedStage(fake);

        await stage.DiarizeEmbedAsync(Rec(), Audio());

        Assert.False(fake.RetainedAudio); // transient-only: nothing stashed
        Assert.Equal(4, fake.RequestAudioLengths.Single()); // it saw the bytes...
    }

    // ---- Contract-violation: an embedding with no matching segment is terminal ----
    [Fact]
    public async Task Embedding_without_matching_segment_is_a_terminal_contract_violation()
    {
        var bad = new DiarizeEmbedResponse(
            segments: [new DiarizedSegment("s1", 0, 1)],
            embeddings: [new SpeakerEmbedding("s1", TestVectors.Axis(0)),
                         new SpeakerEmbedding("s9", TestVectors.Axis(9))], // s9 has no segment
            model: V1Model);
        var stage = new DiarizeEmbedStage(FakeDiarizeEmbedClient.Returning(bad));

        var result = await stage.DiarizeEmbedAsync(Rec(), Audio());
        Assert.False(result.Succeeded);
        Assert.Equal(EnrichmentState.FailedTerminal, result.FailureState);
    }
}
