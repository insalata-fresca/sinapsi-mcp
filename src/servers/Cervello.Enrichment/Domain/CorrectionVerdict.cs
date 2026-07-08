namespace Cervello.Enrichment.Domain;

/// <summary>
/// The four grounded-autonomy outcomes for one proposed text correction (spec
/// <c>text-correction</c> → "Corrections graded by the decision policy"; DESIGN §5.1). The same
/// policy that grades a speaker attribution grades a correction: strong evidence auto-applies to
/// the base diff set; minor+unsure flags; a high-value ambiguity (two plausible resolutions)
/// escalates to an open-point; an unbacked one is omitted (never invented).
/// </summary>
public enum CorrectionOutcome
{
    /// <summary>Strong evidence (glossary/participant/re-ASR match) → applied to the diff set.</summary>
    AutoApplied,

    /// <summary>Minor + uncertain, trivially reversible → applied but marked flagged.</summary>
    Flagged,

    /// <summary>High-value ambiguity (two plausible resolutions) → withheld, open-point enqueued.</summary>
    OpenPoint,

    /// <summary>Evidence absent/contradictory → the span is left as-is, gap flagged (never guessed).</summary>
    Omitted,
}

/// <summary>
/// The decision policy verdict for one proposed correction: the outcome, the diff (present only
/// for an applied outcome), the candidate resolutions (for an open-point), and a reason. An
/// <c>open_point</c>/<c>omitted</c> verdict carries NO diff — nothing is written to the base diff
/// set until the operator answers (or ever, for omit).
/// </summary>
public sealed record CorrectionVerdict
{
    private CorrectionVerdict(
        CorrectionOutcome outcome,
        CorrectionDiff? diff,
        TextSpan span,
        IReadOnlyList<string> candidates,
        string reason)
    {
        Outcome = outcome;
        Diff = diff;
        Span = span;
        Candidates = candidates;
        Reason = reason;
    }

    public CorrectionOutcome Outcome { get; }

    /// <summary>The applied diff — present ONLY for <c>auto_applied</c>/<c>flagged</c>.</summary>
    public CorrectionDiff? Diff { get; }

    /// <summary>The span in the base transcript this verdict concerns.</summary>
    public TextSpan Span { get; }

    /// <summary>For an <c>open_point</c>, the plausible resolutions the operator chooses between.</summary>
    public IReadOnlyList<string> Candidates { get; }

    public string Reason { get; }

    /// <summary>Whether this verdict contributes a diff to the base diff set.</summary>
    public bool IsApplied => Outcome is CorrectionOutcome.AutoApplied or CorrectionOutcome.Flagged;

    public static CorrectionVerdict AutoApplied(CorrectionDiff diff)
    {
        ArgumentNullException.ThrowIfNull(diff);
        return new(CorrectionOutcome.AutoApplied, diff, diff.Span, Array.Empty<string>(),
            $"auto-applied {diff.Kind} '{diff.Before}' → '{diff.After}' @ {diff.Confidence:0.###} ({diff.EvidenceRef})");
    }

    public static CorrectionVerdict Flagged(CorrectionDiff diff)
    {
        ArgumentNullException.ThrowIfNull(diff);
        return new(CorrectionOutcome.Flagged, diff, diff.Span, Array.Empty<string>(),
            $"flagged {diff.Kind} '{diff.Before}' → '{diff.After}' @ {diff.Confidence:0.###} ({diff.EvidenceRef})");
    }

    public static CorrectionVerdict OpenPoint(TextSpan span, IReadOnlyList<string> candidates, string reason)
    {
        ArgumentNullException.ThrowIfNull(span);
        ArgumentNullException.ThrowIfNull(candidates);
        // >= 1 candidate: two-plus is the "plausible resolutions" ambiguity; exactly one is a
        // single-resolution withholding (the escalate-only phase gate — the operator confirms it).
        if (candidates.Count < 1)
            throw new ArgumentException("an open-point correction must carry >= 1 candidate resolution to confirm", nameof(candidates));
        return new(CorrectionOutcome.OpenPoint, null, span, candidates, reason);
    }

    public static CorrectionVerdict Omitted(TextSpan span, string reason)
    {
        ArgumentNullException.ThrowIfNull(span);
        return new(CorrectionOutcome.Omitted, null, span, Array.Empty<string>(), reason);
    }
}
