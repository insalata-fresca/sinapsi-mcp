using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// Live <see cref="IBundleStore"/> that persists the enrichment bundle at
/// <c>inbox/&lt;id&gt;/{data.json, bundle.md}</c> (SCHEMAS §6) in the CT-local working tree of
/// <c>ste/cervello</c>. Write-once per bundle id (idempotent); the graph-writer's R6/R7 self-check
/// runs BEFORE this is called so no binary/body ever reaches the store. Mirrors the
/// <see cref="InMemoryBundleStore"/> contract exactly.
/// </summary>
public sealed class RepoBundleStore : IBundleStore
{
    private readonly string _repoRoot;

    public RepoBundleStore(string repoWorkingTree)
    {
        if (string.IsNullOrWhiteSpace(repoWorkingTree))
            throw new ArgumentException("repoWorkingTree must be non-empty", nameof(repoWorkingTree));
        _repoRoot = repoWorkingTree;
    }

    public string BundlePath(string bundleId, string artifact) =>
        string.IsNullOrEmpty(artifact) ? $"inbox/{bundleId}/" : $"inbox/{bundleId}/{artifact}";

    public Task<bool> ExistsAsync(string bundleId, CancellationToken ct = default) =>
        Task.FromResult(Directory.Exists(Path.Combine(_repoRoot, "inbox", bundleId)));

    public async Task<string> WriteAsync(string bundleId, string dataJson, string bundleMd, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bundleId))
            throw new ArgumentException("bundleId must be non-empty", nameof(bundleId));
        var dir = Path.Combine(_repoRoot, "inbox", bundleId);
        if (Directory.Exists(dir))
            throw new InvalidOperationException($"bundle '{bundleId}' already exists — refusing overwrite");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "data.json"), dataJson, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(dir, "bundle.md"), bundleMd, ct).ConfigureAwait(false);
        return BundlePath(bundleId, "");
    }
}
