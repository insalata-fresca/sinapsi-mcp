using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>Domain-invariant coverage for the contract + domain value types (100% on invariants).</summary>
public sealed class ContractTypeInvariantTests
{
    [Fact]
    public void DiarizeEmbedRequest_rejects_empty_audio_and_format()
    {
        Assert.Throws<ArgumentException>(() => new DiarizeEmbedRequest(ReadOnlyMemory<byte>.Empty, "m4a"));
        Assert.Throws<ArgumentException>(() => new DiarizeEmbedRequest(new byte[] { 1 }, ""));
    }

    [Fact]
    public void DiarizedSegment_rejects_end_before_start_and_empty_speaker()
    {
        Assert.Throws<ArgumentException>(() => new DiarizedSegment("s1", 5.0, 4.0));
        Assert.Throws<ArgumentException>(() => new DiarizedSegment("", 0.0, 1.0));
    }

    [Fact]
    public void SpeakerEmbedding_requires_exactly_256_dims()
    {
        Assert.Throws<ArgumentException>(() => new SpeakerEmbedding("s1", new float[255]));
        Assert.Throws<ArgumentException>(() => new SpeakerEmbedding("s1", new float[257]));
        var ok = new SpeakerEmbedding("s1", new float[256]);
        Assert.Equal(256, ok.Vector.Count);
    }

    [Fact]
    public void DiarizeEmbedModel_requires_non_empty_vad_and_embed()
    {
        Assert.Throws<ArgumentException>(() => new DiarizeEmbedModel("", "e", 256));
        Assert.Throws<ArgumentException>(() => new DiarizeEmbedModel("v", "", 256));
    }

    [Fact]
    public void RecordingRef_rejects_empty_required_fields_and_derives_the_key()
    {
        Assert.Throws<ArgumentException>(() => new RecordingRef("", "sha", "m4a", "fr", true));
        Assert.Throws<ArgumentException>(() => new RecordingRef("id", "", "m4a", "fr", true));
        Assert.Throws<ArgumentException>(() => new RecordingRef("id", "sha", "", "fr", true));
        Assert.Throws<ArgumentException>(() => new RecordingRef("id", "sha", "m4a", "", true));
        Assert.Equal("rec:id:sha", new RecordingRef("id", "sha", "m4a", "fr", true).IdempotencyKey);
    }

    [Fact]
    public void MergedCluster_requires_members_and_256d_centroid()
    {
        var seg = new[] { new DiarizedSegment("s1", 0, 1) };
        Assert.Throws<ArgumentException>(() =>
            new MergedCluster("s1", Array.Empty<string>(), new float[256], seg));
        Assert.Throws<ArgumentException>(() =>
            new MergedCluster("s1", new[] { "s1" }, new float[100], seg));
    }

    [Fact]
    public void BaseTranscript_allows_empty_body_but_requires_language()
    {
        var t = new BaseTranscript("", "fr"); // silent recording is legal
        Assert.Equal("", t.Markdown);
        Assert.Throws<ArgumentException>(() => new BaseTranscript("hi", ""));
    }

    [Fact]
    public void Failure_exceptions_carry_the_right_retryable_flag_and_reason()
    {
        var transient = new DiarizeEmbedTransientException("timeout");
        var terminal = new DiarizeEmbedTerminalException("invalid-audio");
        Assert.True(transient.Retryable);
        Assert.False(terminal.Retryable);
        Assert.Equal("timeout", transient.Reason);
        Assert.Equal("invalid-audio", terminal.Reason);
    }
}
