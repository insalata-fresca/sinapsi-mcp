using Cervello.Watcher.Domain;

namespace Cervello.Watcher.Normalize;

/// <summary>
/// The seam over <c>recordings/manifest.yaml</c> (D7). Core NORMALIZE calls
/// <see cref="AppendAsync"/> — an idempotent read-modify-write. A temp-dir
/// implementation makes WATCH/NORMALIZE fully unit-testable with no git; the prod
/// adapter writes into a CT-local working tree and commits to a watcher-owned branch.
/// </summary>
public interface IManifestStore
{
    /// <summary>
    /// Append one §8 entry iff its <c>id</c> is not already present. Returns true if
    /// the file was changed (entry appended), false if it was a no-op (id already
    /// present) — in which case the file is byte-unchanged.
    /// </summary>
    Task<bool> AppendAsync(ManifestEntry entry, CancellationToken ct);
}
