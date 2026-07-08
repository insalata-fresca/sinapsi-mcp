namespace Cervello.Enrichment.Domain;

/// <summary>
/// One historized correction-map / glossary entry (DESIGN §5.2 step 3, §6.1 learning signal;
/// design Data Model → <c>correction_map</c>). A confirmed <c>before → after</c> mapping the
/// correction pass reads so the same term auto-corrects next time. Seeded by the operator
/// answering a correction open-point (carrying its answer id as <see cref="ConfirmedAnswerId"/>)
/// or by a pre-loaded glossary. The evidence ref an admitted diff carries points back here.
/// </summary>
public sealed record GlossaryEntry
{
    public GlossaryEntry(
        string before,
        string after,
        CorrectionKind kind,
        string? confirmedAnswerId = null)
    {
        if (string.IsNullOrWhiteSpace(before))
            throw new ArgumentException("GlossaryEntry.Before must be non-empty", nameof(before));
        if (string.IsNullOrWhiteSpace(after))
            throw new ArgumentException("GlossaryEntry.After must be non-empty", nameof(after));
        Before = before;
        After = after;
        Kind = kind;
        ConfirmedAnswerId = confirmedAnswerId;
    }

    public string Before { get; }
    public string After { get; }
    public CorrectionKind Kind { get; }

    /// <summary>The <c>human://&lt;answer-id&gt;</c> answer id that confirmed this entry, if any.</summary>
    public string? ConfirmedAnswerId { get; }

    /// <summary>
    /// The evidence ref a diff citing this glossary entry carries — <c>glossary://&lt;before&gt;</c>,
    /// a repo-internal, resolver-checkable ref (SCHEMAS §1 path/registered form).
    /// </summary>
    public string EvidenceRef => $"glossary://{Before}";
}

/// <summary>
/// A resolved participant for a recording — a person the attribution / manifest resolved, with
/// their confirmed aliases (DESIGN §5.2 step 3). A name in the base transcript matching an alias
/// is auto-correctable to the participant's canonical slug, carrying the participant as evidence.
/// </summary>
public sealed record ResolvedParticipant
{
    public ResolvedParticipant(string slug, string canonicalName, IReadOnlyList<string> aliases)
    {
        if (string.IsNullOrWhiteSpace(slug))
            throw new ArgumentException("ResolvedParticipant.Slug must be non-empty", nameof(slug));
        if (string.IsNullOrWhiteSpace(canonicalName))
            throw new ArgumentException("ResolvedParticipant.CanonicalName must be non-empty", nameof(canonicalName));
        ArgumentNullException.ThrowIfNull(aliases);
        Slug = slug;
        CanonicalName = canonicalName;
        Aliases = aliases;
    }

    /// <summary>The dossier slug (e.g. <c>guilhem</c>).</summary>
    public string Slug { get; }

    /// <summary>The canonical name to correct TO (e.g. a mis-heard alias → the person's full name).</summary>
    public string CanonicalName { get; }

    /// <summary>Confirmed aliases/mis-spellings that resolve to this participant.</summary>
    public IReadOnlyList<string> Aliases { get; }

    /// <summary>Whether <paramref name="text"/> matches this participant (canonical name or a confirmed alias), case-insensitively.</summary>
    public bool Matches(string text) =>
        !string.IsNullOrWhiteSpace(text)
        && (string.Equals(text, CanonicalName, StringComparison.OrdinalIgnoreCase)
            || Aliases.Any(a => string.Equals(text, a, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The evidence ref a name-correction to this participant carries — the dossier link.</summary>
    public string EvidenceRef => $"[[{Slug}]]";
}
