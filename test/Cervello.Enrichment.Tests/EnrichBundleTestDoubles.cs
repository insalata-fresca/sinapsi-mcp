using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// Fake link resolver backed by a known-slug set (spec <c>enrichment-linking</c>; lint R4). A slug
/// in the set resolves; anything else needs a stub. No working tree.
/// </summary>
internal sealed class FakeLinkResolver(params string[] existingSlugs) : ILinkResolver
{
    private readonly HashSet<string> _existing = new(existingSlugs, StringComparer.Ordinal);

    public Task<bool> DossierExistsAsync(string slug, CancellationToken ct = default) =>
        Task.FromResult(_existing.Contains(slug));
}
