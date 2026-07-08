using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Policy;

/// <summary>
/// The grounded-autonomy grader for text corrections (spec <c>text-correction</c> → "Corrections
/// graded by the decision policy"; DESIGN §5.1). It is the correction-side sibling of
/// <see cref="DecisionPolicy"/>: it takes an LLM-proposed <see cref="CorrectionCandidate"/> plus
/// the grounding context and routes it to exactly one <see cref="CorrectionOutcome"/>, applying
/// the HARD FLOOR — a correction is admitted as a <see cref="CorrectionDiff"/> ONLY when it is
/// backed by evidence (a glossary entry, a resolved-participant match, or a re-ASR confirmation).
///
/// <list type="bullet">
/// <item><b>auto-apply</b> — the candidate resolves to exactly one evidenced replacement (glossary
///   term, participant alias, or a confident re-ASR).</item>
/// <item><b>escalate (open-point)</b> — two-or-more plausible evidenced resolutions (a name that
///   matches two participants equally) → withheld, the operator chooses.</item>
/// <item><b>omit</b> — no glossary entry, no participant match, no re-ASR clarification → the span
///   is left as-is and flagged, NEVER invented (the no-fabrication floor).</item>
/// </list>
///
/// <para>Phase gate: like <see cref="DecisionPolicy"/>, in <see cref="PolicyPhase.EscalateOnly"/>
/// an otherwise-auto correction is withheld to an open-point — but an OMIT still omits (there is
/// nothing to escalate; escalating a non-correction would create a phantom question).</para>
/// </summary>
public sealed class CorrectionGrader(PolicyPhase phase = PolicyPhase.EscalateOnly)
{
    public PolicyPhase Phase { get; } = phase;

    /// <summary>
    /// Grade one proposed correction against the evidence. <paramref name="reAsr"/> is the re-ASR
    /// result for the span when it was garbled (else null — re-ASR is invoked only for garbled
    /// spans, by the stage).
    /// </summary>
    public CorrectionVerdict Grade(
        CorrectionCandidate candidate,
        CorrectionContext context,
        ReAsrResult? reAsr = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(context);

        // Gather the EVIDENCED resolutions for this candidate, per kind. A resolution the LLM
        // proposed that has no backing evidence is dropped here — it never becomes a diff.
        var evidenced = GatherEvidenced(candidate, context, reAsr);

        // No evidence at all → OMIT (the hard floor). Not overridden by escalate-only: there is
        // no correction to escalate.
        if (evidenced.Count == 0)
            return CorrectionVerdict.Omitted(candidate.Span,
                $"no glossary/participant/re-ASR evidence for '{candidate.Before}' → left as-is (never invented)");

        // Two-or-more DISTINCT evidenced replacements → genuine ambiguity → escalate.
        var distinct = evidenced
            .Select(e => e.After)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (distinct.Count > 1)
            return CorrectionVerdict.OpenPoint(candidate.Span, distinct,
                $"ambiguous correction for '{candidate.Before}': {string.Join(" | ", distinct)} — operator chooses");

        // Exactly one evidenced resolution → build the diff.
        var chosen = evidenced[0];
        var diff = new CorrectionDiff(
            candidate.Span, candidate.Before, chosen.After, candidate.Kind, chosen.Confidence, chosen.EvidenceRef);

        // Escalate-only phase gate: withhold an otherwise-auto correction until validated.
        if (Phase == PolicyPhase.EscalateOnly)
            return CorrectionVerdict.OpenPoint(candidate.Span, [chosen.After],
                $"escalate-only phase: withholding correction '{candidate.Before}' → '{chosen.After}' — auto-apply disabled until validated");

        return CorrectionVerdict.AutoApplied(diff);
    }

    /// <summary>The evidenced resolutions for a candidate (a replacement + its confidence + evidence ref).</summary>
    private static List<(string After, double Confidence, string EvidenceRef)> GatherEvidenced(
        CorrectionCandidate candidate, CorrectionContext context, ReAsrResult? reAsr)
    {
        var result = new List<(string, double, string)>();

        switch (candidate.Kind)
        {
            case CorrectionKind.Term:
                // A glossary entry whose `before` equals the flagged span backs a term correction.
                foreach (var g in context.Glossary)
                    if (g.Kind == CorrectionKind.Term
                        && string.Equals(g.Before, candidate.Before, StringComparison.OrdinalIgnoreCase)
                        && candidate.Candidates.Contains(g.After, StringComparer.Ordinal))
                        result.Add((g.After, candidate.Confidence, g.EvidenceRef));
                break;

            case CorrectionKind.Name:
                // A resolved participant matching the flagged name backs a name correction. Each
                // DISTINCT matching participant is a separate evidenced resolution (→ ambiguity if >1).
                foreach (var p in context.Participants)
                    if (p.Matches(candidate.Before)
                        && candidate.Candidates.Any(c => string.Equals(c, p.CanonicalName, StringComparison.OrdinalIgnoreCase)))
                        result.Add((p.CanonicalName, candidate.Confidence, p.EvidenceRef));
                // A confirmed name in the glossary also backs it.
                foreach (var g in context.Glossary)
                    if (g.Kind == CorrectionKind.Name
                        && string.Equals(g.Before, candidate.Before, StringComparison.OrdinalIgnoreCase)
                        && candidate.Candidates.Contains(g.After, StringComparer.Ordinal))
                        result.Add((g.After, candidate.Confidence, g.EvidenceRef));
                break;

            case CorrectionKind.Garbled:
                // A confident re-ASR clarification is the evidence for a garbled-span correction.
                if (reAsr is { Clarified: true, Text: { } text })
                    result.Add((text, reAsr.Confidence, $"reasr://{text}"));
                break;
        }

        // De-duplicate identical (after, evidence) pairs so a term backed once is not double-counted.
        return result
            .GroupBy(r => (r.Item1, r.Item3))
            .Select(gr => gr.First())
            .ToList();
    }
}
