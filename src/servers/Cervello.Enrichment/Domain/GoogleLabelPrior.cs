namespace Cervello.Enrichment.Domain;

/// <summary>
/// The Google <c>.txt</c> <c>[Speaker N]</c> labels as an OPTIONAL confirming prior (spec
/// <c>enrichment-linking</c> → "Google labels are an optional confirming signal only"; DESIGN
/// §5.2 step 1). The audio diarizer is ALWAYS authoritative for segment boundaries — Google
/// output carries no timestamps. These labels may only RAISE the confirming-prior weight when
/// present and agreeing; their ABSENCE never degrades enrichment.
///
/// <para>This type is deliberately incapable of being a segmentation base: it exposes no
/// segments/timestamps, only a boolean "does a Speaker-N label agree with this resolution" plus a
/// small confirming weight. A recording with plain prose (no labels) yields
/// <see cref="None"/> and changes nothing.</para>
/// </summary>
public sealed record GoogleLabelPrior
{
    /// <summary>The confirming-weight bump a present+agreeing label contributes (small, never decisive).</summary>
    public const double ConfirmingWeight = 0.05;

    private readonly IReadOnlyDictionary<string, string> _speakerToPersonSlug;

    private GoogleLabelPrior(IReadOnlyDictionary<string, string> speakerToPersonSlug)
    {
        _speakerToPersonSlug = speakerToPersonSlug;
    }

    /// <summary>No Google labels present (plain prose) — enrichment proceeds unaffected.</summary>
    public static GoogleLabelPrior None { get; } =
        new(new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>Build from a parsed <c>[Speaker N] → person slug</c> hint map (may be empty).</summary>
    public static GoogleLabelPrior From(IReadOnlyDictionary<string, string> speakerToPersonSlug)
    {
        ArgumentNullException.ThrowIfNull(speakerToPersonSlug);
        return new GoogleLabelPrior(new Dictionary<string, string>(speakerToPersonSlug, StringComparer.Ordinal));
    }

    /// <summary>Whether any labels are present.</summary>
    public bool HasLabels => _speakerToPersonSlug.Count > 0;

    /// <summary>
    /// The confirming-weight bump for resolving <paramref name="speakerLabel"/> to
    /// <paramref name="personSlug"/>: <see cref="ConfirmingWeight"/> iff a present label agrees,
    /// else 0 (absent or disagreeing labels never subtract — they simply don't confirm).
    /// </summary>
    public double ConfirmingBump(string speakerLabel, string personSlug) =>
        _speakerToPersonSlug.TryGetValue(speakerLabel, out var slug)
        && string.Equals(slug, personSlug, StringComparison.Ordinal)
            ? ConfirmingWeight
            : 0.0;
}
