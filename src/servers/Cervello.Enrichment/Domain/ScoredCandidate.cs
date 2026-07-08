namespace Cervello.Enrichment.Domain;

/// <summary>
/// One scored candidate answer for an open-point (spec <c>open-points-mcp</c> → "List pending
/// open-points with decision context"). The list tool exposes these so the operator can decide
/// WITHOUT opening the raw source: a candidate <c>value</c>, its <c>confidence</c> (the match /
/// prior score that produced it), and a short redacted <c>why</c> (e.g. "voice 0.55; filename
/// prior") — never a transcript body / snippet / audio / vector (lint R10 redaction posture).
/// </summary>
public sealed record ScoredCandidate
{
    public ScoredCandidate(string value, double confidence, string why)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("ScoredCandidate.Value must be non-empty", nameof(value));
        if (double.IsNaN(confidence) || confidence < 0 || confidence > 1)
            throw new ArgumentOutOfRangeException(nameof(confidence), confidence,
                "candidate confidence must be in [0,1]");
        if (string.IsNullOrWhiteSpace(why))
            throw new ArgumentException("ScoredCandidate.Why must be non-empty (redacted rationale)", nameof(why));
        Value = value;
        Confidence = confidence;
        Why = why;
    }

    /// <summary>The candidate value the operator may select (e.g. a person slug or a corrected term).</summary>
    public string Value { get; }

    /// <summary>The score behind this candidate (match cosine / prior weight), clamped to [0,1].</summary>
    public double Confidence { get; }

    /// <summary>A one-line redacted rationale — refs + scores only, NO body/snippet/audio (R10).</summary>
    public string Why { get; }

    /// <summary>An unscored candidate (value only) — confidence 0, generic rationale. Used where the
    /// upstream stage produced a bare candidate list with no per-candidate score.</summary>
    public static ScoredCandidate Unscored(string value) =>
        new(value, 0.0, "candidate (no per-candidate score)");
}
