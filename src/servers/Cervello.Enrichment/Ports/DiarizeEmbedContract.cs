namespace Cervello.Enrichment.Ports;

/// <summary>
/// Request/response DTOs for the diarize-embed sidecar contract
/// (spec <c>diarize-embed-sidecar</c> → "Diarize-embed request/response contract").
/// These mirror the wire shape of <c>POST /v1/enrich/diarize-embed</c> exactly so the
/// engine-side <see cref="IDiarizeEmbedClient"/> and E2a's server agree byte-for-byte.
/// </summary>
public sealed record DiarizeEmbedRequest
{
    /// <param name="audio">Recording audio bytes (m4a or wav) — transient inference only.</param>
    /// <param name="format">Container hint the sidecar decodes (<c>m4a</c> | <c>wav</c>).</param>
    /// <param name="minSegmentMs">Optional: drop speech spans shorter than this.</param>
    /// <param name="windowMs">Optional: embedding window size.</param>
    public DiarizeEmbedRequest(
        ReadOnlyMemory<byte> audio,
        string format,
        int? minSegmentMs = null,
        int? windowMs = null)
    {
        if (audio.IsEmpty)
            throw new ArgumentException("DiarizeEmbedRequest.Audio must be non-empty", nameof(audio));
        if (string.IsNullOrWhiteSpace(format))
            throw new ArgumentException("DiarizeEmbedRequest.Format must be non-empty", nameof(format));
        Audio = audio;
        Format = format;
        MinSegmentMs = minSegmentMs;
        WindowMs = windowMs;
    }

    public ReadOnlyMemory<byte> Audio { get; }
    public string Format { get; }
    public int? MinSegmentMs { get; }
    public int? WindowMs { get; }
}

/// <summary>One diarized speech span: <c>{speaker, start, end}</c> (seconds).</summary>
public sealed record DiarizedSegment
{
    public DiarizedSegment(string speaker, double start, double end)
    {
        if (string.IsNullOrWhiteSpace(speaker))
            throw new ArgumentException("DiarizedSegment.Speaker must be non-empty", nameof(speaker));
        if (end < start)
            throw new ArgumentException("DiarizedSegment.End must be >= Start", nameof(end));
        Speaker = speaker;
        Start = start;
        End = end;
    }

    public string Speaker { get; }
    public double Start { get; }
    public double End { get; }
}

/// <summary>
/// The per-speaker centroid embedding: <c>{speaker, vector[192]}</c>. The vector is a
/// 192-d L2-normalised ECAPA-TDNN output (spec: exactly <see cref="ExpectedDim"/> floats).
/// </summary>
public sealed record SpeakerEmbedding
{
    /// <summary>The contract-mandated embedding dimensionality (ECAPA-TDNN, 192-d).</summary>
    public const int ExpectedDim = 192;

    public SpeakerEmbedding(string speaker, IReadOnlyList<float> vector)
    {
        if (string.IsNullOrWhiteSpace(speaker))
            throw new ArgumentException("SpeakerEmbedding.Speaker must be non-empty", nameof(speaker));
        ArgumentNullException.ThrowIfNull(vector);
        if (vector.Count != ExpectedDim)
            throw new ArgumentException(
                $"SpeakerEmbedding.Vector must be {ExpectedDim}-dimensional (got {vector.Count})",
                nameof(vector));
        Speaker = speaker;
        Vector = vector;
    }

    public string Speaker { get; }
    public IReadOnlyList<float> Vector { get; }
}

/// <summary>
/// The serving-stack identity block — lets the engine gate on model version
/// (spec: "Model identity is returned"). v1 ungated stack = silero-VAD + ECAPA, dim 192.
/// </summary>
public sealed record DiarizeEmbedModel
{
    public DiarizeEmbedModel(string vad, string embed, int dim)
    {
        if (string.IsNullOrWhiteSpace(vad))
            throw new ArgumentException("DiarizeEmbedModel.Vad must be non-empty", nameof(vad));
        if (string.IsNullOrWhiteSpace(embed))
            throw new ArgumentException("DiarizeEmbedModel.Embed must be non-empty", nameof(embed));
        Vad = vad;
        Embed = embed;
        Dim = dim;
    }

    public string Vad { get; }
    public string Embed { get; }
    public int Dim { get; }
}

/// <summary>
/// The success (200) response: one <see cref="DiarizedSegment"/> per detected speech
/// span and one <see cref="SpeakerEmbedding"/> per distinct <c>speaker</c>.
/// </summary>
public sealed record DiarizeEmbedResponse
{
    public DiarizeEmbedResponse(
        IReadOnlyList<DiarizedSegment> segments,
        IReadOnlyList<SpeakerEmbedding> embeddings,
        DiarizeEmbedModel model)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(embeddings);
        ArgumentNullException.ThrowIfNull(model);
        Segments = segments;
        Embeddings = embeddings;
        Model = model;
    }

    public IReadOnlyList<DiarizedSegment> Segments { get; }
    public IReadOnlyList<SpeakerEmbedding> Embeddings { get; }
    public DiarizeEmbedModel Model { get; }
}
