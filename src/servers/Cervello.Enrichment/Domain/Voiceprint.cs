using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Domain;

/// <summary>
/// An enrolled voiceprint row exactly as the <c>voiceprint-store</c> spec declares it:
/// <c>{person_slug, vector (256-d), sample_count, enrolled_at, last_match, source_segments[]}</c>.
/// The vector is a running centroid over that person's confirmed segments.
///
/// <para>Confinement (spec + DESIGN §10.4): this type models the CT146 pgvector row. The vector
/// and enrollment audio NEVER enter git, NEVER travel on a shared subject, and NEVER leave
/// CT146. The person dossier records only the <c>voice:</c> mapping RESULT (SCHEMAS §2), never
/// the vector — so this record is never serialised into a bundle or a map file.</para>
/// </summary>
public sealed record Voiceprint
{
    public Voiceprint(
        string personSlug,
        IReadOnlyList<float> centroid,
        int sampleCount,
        DateOnly enrolledAt,
        double? lastMatch,
        IReadOnlyList<string> sourceSegments)
    {
        if (string.IsNullOrWhiteSpace(personSlug))
            throw new ArgumentException("Voiceprint.PersonSlug must be non-empty", nameof(personSlug));
        ArgumentNullException.ThrowIfNull(centroid);
        if (centroid.Count != SpeakerEmbedding.ExpectedDim)
            throw new ArgumentException(
                $"Voiceprint.Centroid must be {SpeakerEmbedding.ExpectedDim}-d (got {centroid.Count})",
                nameof(centroid));
        if (sampleCount < 1)
            throw new ArgumentException("Voiceprint.SampleCount must be >= 1 (an enrolled print has >= 1 sample)",
                nameof(sampleCount));
        ArgumentNullException.ThrowIfNull(sourceSegments);
        PersonSlug = personSlug;
        Centroid = centroid;
        SampleCount = sampleCount;
        EnrolledAt = enrolledAt;
        LastMatch = lastMatch;
        SourceSegments = sourceSegments;
    }

    public string PersonSlug { get; }

    /// <summary>The 256-d running centroid (biometric — CT146-only).</summary>
    public IReadOnlyList<float> Centroid { get; }

    /// <summary>How many confirmed samples the centroid was averaged from.</summary>
    public int SampleCount { get; }

    public DateOnly EnrolledAt { get; }

    /// <summary>The cosine of the most recent match, or null if never matched since enrollment.</summary>
    public double? LastMatch { get; }

    /// <summary>The <c>rec://&lt;id&gt;#&lt;seg&gt;</c> segments that fed the centroid (provenance, CT-side).</summary>
    public IReadOnlyList<string> SourceSegments { get; }

    /// <summary>
    /// The dossier <c>voice:</c> frontmatter value for this print (SCHEMAS §2). Records the
    /// mapping RESULT only — never the vector: <c>enrolled &lt;date&gt;, &lt;n&gt; samples[, last-match
    /// &lt;float&gt;]</c>.
    /// </summary>
    public string DossierVoiceLine()
    {
        var line = $"enrolled {EnrolledAt:yyyy-MM-dd}, {SampleCount} samples";
        if (LastMatch is { } lm)
            line += $", last-match {lm:0.###}";
        return line;
    }
}
