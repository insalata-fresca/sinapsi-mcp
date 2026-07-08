using System.Security.Cryptography;
using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// A fetcher for the bytes behind an external ref (<c>drive://&lt;id&gt;</c> / <c>gmail://&lt;id&gt;</c>).
/// The LIVE implementation calls the gdrive / gmail MCP through the CT121 agentgateway (the same
/// egress the Watcher uses) to download the referenced bytes; a test supplies deterministic bytes.
/// Kept as a seam so <see cref="CtPinStore"/>'s pin-and-hash logic is unit-testable offline and the
/// live external fetch is an L2 on-CT concern.
/// </summary>
public interface IExternalBlobFetcher
{
    /// <summary>Fetch the bytes behind an external ref for pinning (transient — used only to hash + store).</summary>
    Task<ReadOnlyMemory<byte>> FetchAsync(string externalRef, CancellationToken ct = default);
}

/// <summary>
/// Live CT-side <see cref="IPinStore"/> (SCHEMAS §1 <c>pin://</c>; lint R11). At graph-add the writer
/// PINS external evidence so a merged map line cites <c>pin://&lt;sha256&gt;</c> with the external ref
/// kept as provenance only. This adapter fetches the referenced bytes (via
/// <see cref="IExternalBlobFetcher"/>), writes them into the CT-local pin blob store at
/// <c>&lt;pinDir&gt;/&lt;sha256&gt;</c>, and returns the sha256 the <c>pin://</c> ref uses.
///
/// <para><b>L2 note:</b> the sha256 computation + on-CT blob write are exercised offline (a test
/// injects a deterministic fetcher). The LIVE external fetch — drive:// / gmail:// via the
/// agentgateway — is an L2 on-CT step (needs the live gateway + scoped identity); it is NOT stubbed
/// to a fake success here.</para>
/// </summary>
public sealed class CtPinStore : IPinStore
{
    private readonly IExternalBlobFetcher _fetcher;
    private readonly string _pinDir;

    public CtPinStore(IExternalBlobFetcher fetcher, string pinDir)
    {
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
        if (string.IsNullOrWhiteSpace(pinDir))
            throw new ArgumentException("pinDir must be non-empty", nameof(pinDir));
        _pinDir = pinDir;
    }

    public async Task<string> PinAsync(string externalRef, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(externalRef))
            throw new ArgumentException("externalRef must be non-empty", nameof(externalRef));

        var bytes = await _fetcher.FetchAsync(externalRef, ct).ConfigureAwait(false);
        var sha = Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant();

        Directory.CreateDirectory(_pinDir);
        var blobPath = Path.Combine(_pinDir, sha);
        // Content-addressed: if the blob is already pinned, the write is a no-op (idempotent).
        if (!File.Exists(blobPath))
            await File.WriteAllBytesAsync(blobPath, bytes.ToArray(), ct).ConfigureAwait(false);
        return sha;
    }
}
