using Cervello.Enrichment.Domain;

namespace Cervello.Enrichment.Ports;

/// <summary>
/// Port for the historized correction map / glossary (design Data Model → <c>correction_map</c>;
/// DESIGN §5.2 step 3, §6.1 learning signal). The correction pass reads it to build its grounding
/// context; an operator answer to a correction open-point WRITES an entry so the same term
/// auto-corrects next time (spec <c>text-correction</c> → "Operator answer feeds the correction
/// map"). CT146 Postgres in prod; in-memory in tests. Never git-side.
/// </summary>
public interface ICorrectionMapStore
{
    /// <summary>The current glossary entries (the historized correction map).</summary>
    Task<IReadOnlyList<GlossaryEntry>> GetGlossaryAsync(CancellationToken ct = default);

    /// <summary>
    /// Record a confirmed correction (operator answer). Idempotent on <c>(before, kind)</c>; a
    /// later answer for the same term updates it. This is the learning signal that makes a
    /// previously-escalated term auto-correct on the next recording.
    /// </summary>
    Task UpsertAsync(GlossaryEntry entry, CancellationToken ct = default);
}
