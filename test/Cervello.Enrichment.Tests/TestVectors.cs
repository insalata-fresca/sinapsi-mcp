using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// Synthetic 192-d embedding helpers. NO personal audio and NO real biometric vectors ever
/// appear in these tests — only deterministically constructed synthetic vectors whose pairwise
/// cosine we control, so we can prove cluster-merge collapses an over-split speaker and never
/// merges two distinct embeddings.
/// </summary>
internal static class TestVectors
{
    public const int Dim = SpeakerEmbedding.ExpectedDim; // 192

    /// <summary>A vector that is 1.0 on axis <paramref name="axis"/> and 0 elsewhere (unit).</summary>
    public static float[] Axis(int axis)
    {
        var v = new float[Dim];
        v[axis % Dim] = 1f;
        return v;
    }

    /// <summary>
    /// A vector close to <see cref="Axis"/> <paramref name="axis"/> but tilted toward a second
    /// axis so its cosine with the pure axis vector is exactly <paramref name="targetCosine"/>.
    /// Used to synthesise "same speaker, over-split" (high cosine) and "distinct" (low cosine)
    /// centroids deterministically.
    /// </summary>
    public static float[] TiltedFromAxis(int axis, int tiltAxis, double targetCosine)
    {
        if (axis == tiltAxis) throw new ArgumentException("axis and tiltAxis must differ");
        // v = cos*e_axis + sin*e_tilt  → unit vector, cosine with e_axis == cos.
        var cos = (float)targetCosine;
        var sin = (float)System.Math.Sqrt(System.Math.Max(0.0, 1.0 - targetCosine * targetCosine));
        var v = new float[Dim];
        v[axis % Dim] = cos;
        v[tiltAxis % Dim] = sin;
        return v;
    }
}
