namespace Cervello.Enrichment.Ports;

/// <summary>
/// Port for persisting the enrichment bundle at <c>inbox/&lt;id&gt;/{bundle.md, data.json}</c>
/// (SCHEMAS §6). The live adapter writes the git-side files; a fake models the paths + contents
/// in tests. Written once per bundle id (idempotent); the writer's R6/R7 self-check runs BEFORE
/// this is called so no binary/body ever reaches the store.
/// </summary>
public interface IBundleStore
{
    /// <summary>The repo-relative path for a bundle artifact (<c>data.json</c> | <c>bundle.md</c>).</summary>
    string BundlePath(string bundleId, string artifact);

    /// <summary>True if a bundle already exists for this id (idempotency).</summary>
    Task<bool> ExistsAsync(string bundleId, CancellationToken ct = default);

    /// <summary>Persist the two bundle artifacts. Returns the <c>inbox/&lt;id&gt;/</c> dir path.</summary>
    Task<string> WriteAsync(string bundleId, string dataJson, string bundleMd, CancellationToken ct = default);
}
