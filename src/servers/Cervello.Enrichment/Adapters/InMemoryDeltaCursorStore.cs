using System.Collections.Concurrent;
using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// In-memory <see cref="IDeltaCursorStore"/> (offline slice / tests / a fake-mode host). Holds the
/// per-caller, per-intent baseline <c>as_of</c> in a concurrent map — deterministic, no DB, no
/// personal data (the key is an opaque identity hash; the value is a timestamp). Mirrors the live
/// Pg store's contract exactly.
/// </summary>
public sealed class InMemoryDeltaCursorStore : IDeltaCursorStore
{
    private readonly ConcurrentDictionary<string, string> _cursors = new(StringComparer.Ordinal);

    public Task<string?> GetBaselineAsync(string callerKey, string intent, CancellationToken ct = default) =>
        Task.FromResult(_cursors.TryGetValue(Key(callerKey, intent), out var v) ? v : null);

    public Task AdvanceAsync(string callerKey, string intent, string asOf, CancellationToken ct = default)
    {
        _cursors[Key(callerKey, intent)] = asOf;
        return Task.CompletedTask;
    }

    private static string Key(string callerKey, string intent) => $"{callerKey}|{intent}";
}
