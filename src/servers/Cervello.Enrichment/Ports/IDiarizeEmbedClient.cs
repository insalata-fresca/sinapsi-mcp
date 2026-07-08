namespace Cervello.Enrichment.Ports;

/// <summary>
/// Port for the diarize-embed sidecar (spec <c>diarize-embed-sidecar</c>). The engine
/// obtains per-speaker segmentation + per-speaker centroid embeddings by calling a single
/// endpoint — modelled here as one method so the stages are unit-testable offline against
/// a fake ("Client is a swappable seam").
///
/// <para>Confinement: audio flows in as a transient request payload; only the derived
/// <see cref="DiarizeEmbedResponse.Segments"/> + <see cref="DiarizeEmbedResponse.Embeddings"/>
/// return. Failures surface as <see cref="DiarizeEmbedTransientException"/> (retry under the
/// same idempotency key) or <see cref="DiarizeEmbedTerminalException"/> (mark
/// <c>failed_terminal</c> with a reason) — the caller MUST NOT fabricate segments/embeddings.</para>
/// </summary>
public interface IDiarizeEmbedClient
{
    /// <summary>
    /// Diarize + embed a recording. Returns one segment per speech span and one centroid
    /// embedding per distinct speaker.
    /// </summary>
    /// <exception cref="DiarizeEmbedTransientException">Timeout / 5xx / connection reset — retryable.</exception>
    /// <exception cref="DiarizeEmbedTerminalException">Undecodable audio / 4xx contract violation — terminal.</exception>
    Task<DiarizeEmbedResponse> DiarizeEmbedAsync(DiarizeEmbedRequest request, CancellationToken ct = default);
}

/// <summary>
/// Base for sidecar-call failures. Carries whether the failure is retryable so the pipeline
/// maps it onto <c>failed_retryable</c> vs <c>failed_terminal</c> (SCHEMAS §5) without
/// re-classifying HTTP semantics at the call site.
/// </summary>
public abstract class DiarizeEmbedException : Exception
{
    protected DiarizeEmbedException(string reason, bool retryable, Exception? inner = null)
        : base(reason, inner)
    {
        Reason = reason;
        Retryable = retryable;
    }

    /// <summary>Machine/human reason string (also the <c>failed_terminal</c> reason).</summary>
    public string Reason { get; }

    public bool Retryable { get; }
}

/// <summary>Transient (timeout / 5xx / connection reset) → <c>failed_retryable</c>.</summary>
public sealed class DiarizeEmbedTransientException(string reason, Exception? inner = null)
    : DiarizeEmbedException(reason, retryable: true, inner);

/// <summary>Terminal (undecodable audio / 4xx contract violation) → <c>failed_terminal</c>.</summary>
public sealed class DiarizeEmbedTerminalException(string reason, Exception? inner = null)
    : DiarizeEmbedException(reason, retryable: false, inner);
