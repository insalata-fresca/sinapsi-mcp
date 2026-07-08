using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cervello.Enrichment.Pipeline.Stages;

/// <summary>
/// Runs the audio diarizer + embedder via <see cref="IDiarizeEmbedClient"/> and assembles the
/// per-speaker <see cref="SpeakerCluster"/>s (spec <c>diarize-embed-sidecar</c>). The audio
/// diarizer is ALWAYS run — Google <c>[Speaker N]</c> labels are an optional prior only
/// (E0.5 §4). On a sidecar failure the stage classifies transient vs terminal and maps to
/// <c>failed_retryable</c> / <c>failed_terminal</c> (SCHEMAS §5), NEVER fabricating segments
/// or embeddings.
/// </summary>
public sealed class DiarizeEmbedStage(IDiarizeEmbedClient client, ILogger<DiarizeEmbedStage>? logger = null)
{
    private readonly IDiarizeEmbedClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly ILogger _log = logger ?? NullLogger<DiarizeEmbedStage>.Instance;

    /// <param name="audio">Transient audio bytes (fetched from the CT staging blob store).</param>
    public async Task<DiarizeEmbedStageResult> DiarizeEmbedAsync(
        RecordingRef recording,
        ReadOnlyMemory<byte> audio,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recording);
        if (audio.IsEmpty)
            throw new ArgumentException("diarize-embed requires non-empty audio bytes", nameof(audio));

        var request = new DiarizeEmbedRequest(audio, recording.Format);

        DiarizeEmbedResponse response;
        try
        {
            response = await _client.DiarizeEmbedAsync(request, ct).ConfigureAwait(false);
        }
        catch (DiarizeEmbedTransientException ex)
        {
            _log.LogWarning("diarize-embed transient failure for {Key}: {Reason} → failed_retryable",
                recording.IdempotencyKey, ex.Reason);
            return DiarizeEmbedStageResult.Failed(EnrichmentState.FailedRetryable, ex.Reason);
        }
        catch (DiarizeEmbedTerminalException ex)
        {
            _log.LogError("diarize-embed terminal failure for {Key}: {Reason} → failed_terminal",
                recording.IdempotencyKey, ex.Reason);
            return DiarizeEmbedStageResult.Failed(EnrichmentState.FailedTerminal, ex.Reason);
        }

        IReadOnlyList<SpeakerCluster> clusters;
        try
        {
            clusters = AssembleClusters(response);
        }
        catch (DiarizeEmbedTerminalException ex)
        {
            // A malformed response (segment/embedding mismatch) is a terminal contract violation.
            _log.LogError("diarize-embed malformed response for {Key}: {Reason} → failed_terminal",
                recording.IdempotencyKey, ex.Reason);
            return DiarizeEmbedStageResult.Failed(EnrichmentState.FailedTerminal, ex.Reason);
        }

        _log.LogInformation("diarize-embed {Key}: {Segs} segments, {Clusters} local clusters (model vad={Vad} embed={Embed} dim={Dim})",
            recording.IdempotencyKey, response.Segments.Count, clusters.Count,
            response.Model.Vad, response.Model.Embed, response.Model.Dim);
        return DiarizeEmbedStageResult.Ok(clusters, response.Model);
    }

    /// <summary>
    /// Join the response's segments (grouped by speaker) with the per-speaker embeddings into
    /// <see cref="SpeakerCluster"/>s. A speaker with an embedding but no segment, or a segment
    /// speaker with no embedding, is a contract violation (terminal).
    /// </summary>
    private static IReadOnlyList<SpeakerCluster> AssembleClusters(DiarizeEmbedResponse response)
    {
        var segsBySpeaker = response.Segments
            .GroupBy(s => s.Speaker, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Start).ToList(), StringComparer.Ordinal);

        var clusters = new List<SpeakerCluster>(response.Embeddings.Count);
        foreach (var emb in response.Embeddings)
        {
            if (!segsBySpeaker.TryGetValue(emb.Speaker, out var segs) || segs.Count == 0)
                throw new DiarizeEmbedTerminalException(
                    $"contract violation: embedding for speaker '{emb.Speaker}' has no matching segments");
            clusters.Add(new SpeakerCluster(emb.Speaker, emb.Vector, segs));
        }

        // Every segment speaker must have an embedding (else attribution can't match it).
        foreach (var speaker in segsBySpeaker.Keys)
            if (response.Embeddings.All(e => !string.Equals(e.Speaker, speaker, StringComparison.Ordinal)))
                throw new DiarizeEmbedTerminalException(
                    $"contract violation: segment speaker '{speaker}' has no embedding");

        return clusters;
    }
}

/// <summary>Outcome of <see cref="DiarizeEmbedStage.DiarizeEmbedAsync"/>.</summary>
public sealed record DiarizeEmbedStageResult
{
    private DiarizeEmbedStageResult(
        bool succeeded,
        IReadOnlyList<SpeakerCluster> clusters,
        DiarizeEmbedModel? model,
        EnrichmentState? failureState,
        string? reason)
    {
        Succeeded = succeeded;
        Clusters = clusters;
        Model = model;
        FailureState = failureState;
        Reason = reason;
    }

    public bool Succeeded { get; }
    public IReadOnlyList<SpeakerCluster> Clusters { get; }
    public DiarizeEmbedModel? Model { get; }

    /// <summary><c>failed_retryable</c> or <c>failed_terminal</c> when <see cref="Succeeded"/> is false.</summary>
    public EnrichmentState? FailureState { get; }

    public string? Reason { get; }

    internal static DiarizeEmbedStageResult Ok(IReadOnlyList<SpeakerCluster> clusters, DiarizeEmbedModel model) =>
        new(true, clusters, model, null, null);

    internal static DiarizeEmbedStageResult Failed(EnrichmentState failureState, string reason) =>
        new(false, Array.Empty<SpeakerCluster>(), null, failureState, reason);
}
