namespace Cervello.Enrichment.Ports;

/// <summary>
/// Port for the CT-side pin blob store (SCHEMAS §1 <c>pin://</c>; lint R11). At graph-add the
/// writer PINS external evidence (<c>drive://</c>/<c>gmail://</c>) so a merged map line cites
/// <c>pin://&lt;sha256&gt;</c> with the external ref kept as provenance only. Returns the sha256
/// of the pinned bytes. In-memory in tests (no network / no real Drive/Gmail fetch).
/// </summary>
public interface IPinStore
{
    /// <summary>Pin the bytes behind an external ref; returns the sha256 the <c>pin://</c> ref uses.</summary>
    Task<string> PinAsync(string externalRef, CancellationToken ct = default);
}
