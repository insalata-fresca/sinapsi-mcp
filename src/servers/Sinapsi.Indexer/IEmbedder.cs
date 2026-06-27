namespace Sinapsi.Indexer;

/// <summary>Local text → dense vector embedder (the hybrid-search half).
/// Implementations run entirely in-process (no external service / no SaaS — so
/// indexed content never leaves the host). Returns an L2-normalised vector of
/// length <see cref="Dim"/>.</summary>
public interface IEmbedder
{
    int Dim { get; }
    float[] Embed(string text);
}
