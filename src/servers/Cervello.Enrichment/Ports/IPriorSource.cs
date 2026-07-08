namespace Cervello.Enrichment.Ports;

/// <summary>
/// Port for the org-chart / filename prior (spec <c>speaker-attribution</c>; discovery Q6). The
/// resolver treats priors as an ABSTRACT candidate-set input — the recording's filename +
/// manifest participants for v1, with a structured org-chart source additive later — so this
/// seam is unit-testable offline against a fake. A prior narrows candidates and, in the decision
/// policy, either AGREES with, CONFLICTS with, or is silent about the voice match. It NEVER
/// resolves an identity by itself.
/// </summary>
public interface IPriorSource
{
    /// <summary>
    /// The candidate set the prior suggests for a recording (person slugs), and whether the prior
    /// is a STRONG one for this recording (filename names a person explicitly / manifest lists
    /// participants). An empty candidate set means "no prior identifies this speaker".
    /// </summary>
    Task<PriorCandidates> GetPriorAsync(string recordingId, CancellationToken ct = default);
}

/// <summary>
/// The prior's suggestion for a recording: the candidate person slugs and whether it is a strong
/// prior (used to detect a ≥ auto-band voice match that CONFLICTS with a strong prior → escalate).
/// </summary>
public sealed record PriorCandidates
{
    public PriorCandidates(IReadOnlyList<string> candidatePersonSlugs, bool isStrong)
    {
        ArgumentNullException.ThrowIfNull(candidatePersonSlugs);
        CandidatePersonSlugs = candidatePersonSlugs;
        IsStrong = isStrong;
    }

    /// <summary>Empty, non-strong prior — "no prior identifies this speaker".</summary>
    public static PriorCandidates None { get; } = new(Array.Empty<string>(), isStrong: false);

    /// <summary>The person slugs the prior narrows to (may be empty).</summary>
    public IReadOnlyList<string> CandidatePersonSlugs { get; }

    /// <summary>Whether the prior is strong enough to CONFLICT-escalate a high voice match (DESIGN §5.1).</summary>
    public bool IsStrong { get; }

    /// <summary>Whether the prior includes <paramref name="personSlug"/> (prior agrees).</summary>
    public bool Includes(string personSlug) =>
        !string.IsNullOrWhiteSpace(personSlug)
        && CandidatePersonSlugs.Contains(personSlug, StringComparer.Ordinal);

    public bool IsEmpty => CandidatePersonSlugs.Count == 0;
}
