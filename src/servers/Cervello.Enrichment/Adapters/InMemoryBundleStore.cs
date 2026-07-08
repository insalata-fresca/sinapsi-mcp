using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// In-memory <see cref="IBundleStore"/> for tests: models <c>inbox/&lt;id&gt;/{data.json,bundle.md}</c>
/// without a working tree, and REFUSES to overwrite an existing bundle (write-once). Exposes the
/// stored artifacts so a test can validate the SCHEMAS §6 shape.
/// </summary>
public sealed class InMemoryBundleStore : IBundleStore
{
    private readonly Dictionary<string, (string DataJson, string BundleMd)> _byId = new(StringComparer.Ordinal);

    public string BundlePath(string bundleId, string artifact) =>
        string.IsNullOrEmpty(artifact) ? $"inbox/{bundleId}/" : $"inbox/{bundleId}/{artifact}";

    public Task<bool> ExistsAsync(string bundleId, CancellationToken ct = default) =>
        Task.FromResult(_byId.ContainsKey(bundleId));

    public Task<string> WriteAsync(string bundleId, string dataJson, string bundleMd, CancellationToken ct = default)
    {
        if (_byId.ContainsKey(bundleId))
            throw new InvalidOperationException($"bundle '{bundleId}' already exists — refusing overwrite");
        _byId[bundleId] = (dataJson, bundleMd);
        return Task.FromResult(BundlePath(bundleId, ""));
    }

    public (string DataJson, string BundleMd)? Read(string bundleId) =>
        _byId.TryGetValue(bundleId, out var v) ? v : null;
}
