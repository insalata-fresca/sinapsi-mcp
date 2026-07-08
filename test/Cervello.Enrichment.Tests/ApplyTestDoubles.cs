using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// Fake map-PR writer (the "map-PR-writer fake" the mission names). Captures the assembled
/// <see cref="MapReviewPr"/> so a test can assert its shape (back-links, stubs, mutations) without
/// opening a real PR / touching the network. No git, no deploy.
/// </summary>
internal sealed class FakeMapPrWriter : IMapPrWriter
{
    public MapReviewPr? LastPr { get; private set; }
    public int Opened { get; private set; }

    public Task<MapPrHandle> OpenPrAsync(MapReviewPr pr, CancellationToken ct = default)
    {
        LastPr = pr;
        Opened++;
        return Task.FromResult(new MapPrHandle(pr.Branch, pr.Title, Number: 100 + Opened));
    }
}

/// <summary>Fake pin store: deterministic sha for an external ref (no real fetch).</summary>
internal sealed class FakePinStore : IPinStore
{
    public List<string> Pinned { get; } = [];

    public Task<string> PinAsync(string externalRef, CancellationToken ct = default)
    {
        Pinned.Add(externalRef);
        // A stable synthetic sha256-shaped id derived from the ref (no real bytes fetched).
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(externalRef))).ToLowerInvariant();
        return Task.FromResult(sha);
    }
}
