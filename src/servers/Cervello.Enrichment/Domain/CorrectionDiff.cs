namespace Cervello.Enrichment.Domain;

/// <summary>
/// The KIND of a grounded text correction (spec <c>text-correction</c> → "Grounded correction
/// pass, diffs not rewrites"). Every correction is one of these three; the engine SHALL NEVER
/// emit a wholesale rewrite of the base transcript, only individual reviewable diffs.
/// </summary>
public enum CorrectionKind
{
    /// <summary>A person NAME corrected to a resolved participant / confirmed alias.</summary>
    Name,

    /// <summary>A domain TERM corrected to a glossary entry (e.g. "Total Energies" → "TotalEnergies").</summary>
    Term,

    /// <summary>A GARBLED span clarified by selective re-ASR of just that span.</summary>
    Garbled,
}

/// <summary>
/// A single proposed correction against the base transcript (spec <c>text-correction</c>):
/// <c>{span, before, after, kind, confidence, evidence_ref}</c>. It is a *diff*, never a rewritten
/// transcript — <see cref="Before"/> is the exact base substring and <see cref="After"/> the
/// replacement, both reviewable in isolation. The hard floor (DESIGN §5.1): a diff MUST carry an
/// <see cref="EvidenceRef"/> — a glossary entry, a resolved-participant match, or a re-ASR
/// confirmation. An unbacked change is never expressed as a <see cref="CorrectionDiff"/>; it is
/// omitted or escalated, never invented.
/// </summary>
public sealed record CorrectionDiff
{
    public CorrectionDiff(
        TextSpan span,
        string before,
        string after,
        CorrectionKind kind,
        double confidence,
        string evidenceRef)
    {
        ArgumentNullException.ThrowIfNull(span);
        if (before is null)
            throw new ArgumentNullException(nameof(before));
        if (string.IsNullOrEmpty(after))
            throw new ArgumentException("a correction 'after' must be non-empty (a deletion is not a correction)", nameof(after));
        if (string.Equals(before, after, StringComparison.Ordinal))
            throw new ArgumentException("a correction diff must change the text (before == after)", nameof(after));
        if (confidence is < 0.0 or > 1.0)
            throw new ArgumentOutOfRangeException(nameof(confidence), confidence, "confidence must be in [0,1]");
        // The no-fabrication floor, enforced in the constructor so an unbacked diff cannot exist.
        if (string.IsNullOrWhiteSpace(evidenceRef))
            throw new ArgumentException(
                "a CorrectionDiff MUST carry an evidence_ref (glossary / participant / re-ASR) — " +
                "an unbacked correction is never invented (DESIGN §5.1 no-fabrication floor)",
                nameof(evidenceRef));
        Span = span;
        Before = before;
        After = after;
        Kind = kind;
        Confidence = confidence;
        EvidenceRef = evidenceRef;
    }

    /// <summary>The character span in the base transcript this diff replaces.</summary>
    public TextSpan Span { get; }

    /// <summary>The exact base-transcript substring being replaced.</summary>
    public string Before { get; }

    /// <summary>The replacement text.</summary>
    public string After { get; }

    public CorrectionKind Kind { get; }

    /// <summary>Confidence in the correction (used by the decision policy grading).</summary>
    public double Confidence { get; }

    /// <summary>
    /// The evidence backing this diff — a glossary/correction-map ref, a resolved-participant
    /// ref (<c>[[person]]</c>/dossier path), or a re-ASR <c>rec://…#span</c> ref. Never empty.
    /// </summary>
    public string EvidenceRef { get; }
}

/// <summary>A half-open character span <c>[Start, End)</c> in the base transcript.</summary>
public sealed record TextSpan
{
    public TextSpan(int start, int end)
    {
        if (start < 0)
            throw new ArgumentOutOfRangeException(nameof(start), start, "span start must be >= 0");
        if (end <= start)
            throw new ArgumentOutOfRangeException(nameof(end), end, "span end must be > start");
        Start = start;
        End = end;
    }

    public int Start { get; }
    public int End { get; }
    public int Length => End - Start;
}
