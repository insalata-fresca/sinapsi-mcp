namespace Cervello.Enrichment.Math;

/// <summary>
/// Cosine similarity over speaker-embedding vectors. Vectors from the sidecar are
/// L2-normalised pyannote/wespeaker outputs, but this computes a full cosine (divides by norms) so it
/// is correct for synthetic test vectors too — and returns 0 for a degenerate zero vector
/// rather than NaN.
/// </summary>
public static class Cosine
{
    public static double Similarity(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Count != b.Count)
            throw new ArgumentException($"vector length mismatch ({a.Count} vs {b.Count})");

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Count; i++)
        {
            double x = a[i], y = b[i];
            dot += x * y;
            na += x * x;
            nb += y * y;
        }

        if (na == 0 || nb == 0) return 0.0;
        return dot / (System.Math.Sqrt(na) * System.Math.Sqrt(nb));
    }

    /// <summary>Element-wise mean of a non-empty set of equal-length vectors (the centroid).</summary>
    public static float[] Centroid(IReadOnlyList<IReadOnlyList<float>> vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        if (vectors.Count == 0)
            throw new ArgumentException("cannot take a centroid of zero vectors", nameof(vectors));

        var dim = vectors[0].Count;
        var acc = new double[dim];
        foreach (var v in vectors)
        {
            if (v.Count != dim)
                throw new ArgumentException("centroid inputs must be equal length");
            for (var i = 0; i < dim; i++) acc[i] += v[i];
        }

        var centroid = new float[dim];
        for (var i = 0; i < dim; i++) centroid[i] = (float)(acc[i] / vectors.Count);
        return centroid;
    }
}
