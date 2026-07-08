using Cervello.Enrichment.Domain;

namespace Cervello.Enrichment.Ports;

/// <summary>
/// Port for the grounded correction pass (spec <c>text-correction</c> → "Grounded correction
/// pass, diffs not rewrites"; DESIGN §5.2 step 3). The LLM (via brain-api) sees the base
/// transcript + the historized correction map/glossary + the resolved participant set and
/// PROPOSES corrections as <see cref="CorrectionCandidate"/>s — spans it thinks are wrong plus
/// the resolution(s) it considered. It NEVER returns a rewritten transcript.
///
/// <para>Critically, the LLM's proposal is NOT trusted on its own: the <c>CorrectionStage</c>
/// evidence-gates every candidate against the glossary / resolved participants / re-ASR before it
/// can become a <see cref="CorrectionDiff"/>. A candidate the LLM invents with no backing
/// resolves to OMIT, never a written correction. The fake stands in for brain-api in tests.</para>
/// </summary>
public interface ICorrectionLlm
{
    /// <summary>
    /// Propose corrections over the base transcript, given the correction context (glossary +
    /// resolved participants). The returned candidates are UNGATED proposals — the stage grades
    /// them. No live endpoint in tests.
    /// </summary>
    Task<IReadOnlyList<CorrectionCandidate>> ProposeAsync(
        string baseText,
        CorrectionContext context,
        CancellationToken ct = default);
}

/// <summary>
/// One correction the LLM proposes: the span it flags, the base substring, one-or-more candidate
/// replacements it considered, and the kind. The stage decides — auto / flag / escalate / omit —
/// after checking each candidate against the evidence. Multiple <see cref="Candidates"/> with
/// comparable support is the "two plausible resolutions" ambiguity that escalates.
/// </summary>
public sealed record CorrectionCandidate
{
    public CorrectionCandidate(
        TextSpan span,
        string before,
        IReadOnlyList<string> candidates,
        CorrectionKind kind,
        double confidence)
    {
        ArgumentNullException.ThrowIfNull(span);
        if (string.IsNullOrEmpty(before))
            throw new ArgumentException("CorrectionCandidate.Before must be non-empty", nameof(before));
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
            throw new ArgumentException("a correction candidate must propose >= 1 replacement", nameof(candidates));
        if (confidence is < 0.0 or > 1.0)
            throw new ArgumentOutOfRangeException(nameof(confidence), confidence, "confidence must be in [0,1]");
        Span = span;
        Before = before;
        Candidates = candidates;
        Kind = kind;
        Confidence = confidence;
    }

    public TextSpan Span { get; }
    public string Before { get; }

    /// <summary>The replacement(s) the LLM proposed (≥1); ≥2 comparable ones = ambiguity.</summary>
    public IReadOnlyList<string> Candidates { get; }

    public CorrectionKind Kind { get; }
    public double Confidence { get; }
}

/// <summary>
/// The grounding context handed to the correction pass: the historized correction map/glossary
/// and the resolved-participant set for this recording. This is the ONLY evidence the stage
/// admits a diff against — the LLM's own confidence is never sufficient by itself.
/// </summary>
public sealed record CorrectionContext
{
    public CorrectionContext(
        IReadOnlyList<GlossaryEntry> glossary,
        IReadOnlyList<ResolvedParticipant> participants)
    {
        ArgumentNullException.ThrowIfNull(glossary);
        ArgumentNullException.ThrowIfNull(participants);
        Glossary = glossary;
        Participants = participants;
    }

    public static CorrectionContext Empty { get; } =
        new(Array.Empty<GlossaryEntry>(), Array.Empty<ResolvedParticipant>());

    public IReadOnlyList<GlossaryEntry> Glossary { get; }
    public IReadOnlyList<ResolvedParticipant> Participants { get; }
}
