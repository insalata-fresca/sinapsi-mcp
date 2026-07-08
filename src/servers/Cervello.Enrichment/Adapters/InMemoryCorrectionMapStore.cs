using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// In-memory <see cref="ICorrectionMapStore"/> for tests (mirrors the CT146 <c>correction_map</c>
/// table's contract without a live DB). Models the historized glossary and the "operator answer
/// feeds the map" learning signal; keyed by <c>(before, kind)</c> so a later confirmation updates
/// the same term. Never touches git.
/// </summary>
public sealed class InMemoryCorrectionMapStore : ICorrectionMapStore
{
    private readonly Dictionary<(string, CorrectionKind), GlossaryEntry> _entries = new();

    public InMemoryCorrectionMapStore(IEnumerable<GlossaryEntry>? seed = null)
    {
        if (seed is null) return;
        foreach (var e in seed) _entries[(e.Before, e.Kind)] = e;
    }

    public Task<IReadOnlyList<GlossaryEntry>> GetGlossaryAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<GlossaryEntry>>(_entries.Values.ToList());

    public Task UpsertAsync(GlossaryEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entries[(entry.Before, entry.Kind)] = entry;
        return Task.CompletedTask;
    }
}
