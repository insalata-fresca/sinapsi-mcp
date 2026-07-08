namespace Cervello.Enrichment.Ports;

/// <summary>
/// Port that resolves whether a <c>[[slug]]</c> link targets an existing dossier (lint R4;
/// SCHEMAS §1 path resolver). The enrich/link + apply stages use it to decide whether a proposed
/// link resolves or must be declared as a <c>stub: true</c> file in the same PR (R4). A fake
/// backed by a known-slug set stands in for the CT-local resolver in tests.
/// </summary>
public interface ILinkResolver
{
    /// <summary>Whether a dossier exists at <c>map/**/{slug}.md</c> (or wherever the resolver checks).</summary>
    Task<bool> DossierExistsAsync(string slug, CancellationToken ct = default);
}
