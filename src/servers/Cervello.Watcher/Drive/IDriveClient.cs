using Cervello.Watcher.Domain;

namespace Cervello.Watcher.Drive;

/// <summary>One page of Changes-feed results plus the token to fetch the next.</summary>
public sealed record ChangePage(IReadOnlyList<DriveChange> Changes, string? NextPageToken, string? NewStartPageToken);

/// <summary>
/// The seam over Google Drive that WATCH/NORMALIZE depend on. A FakeDriveClient
/// implements this for all core unit tests; McpGdriveClient (M6-refine) is the
/// real implementation, calling the existing homelab `gdrive` MCP through the
/// CT121 agentgateway as a scoped machine identity — no Google credential in
/// this process. Keeping the surface this small is what makes the acceptance
/// logic (pairing + idempotency) fully testable with no Google/no gateway.
/// </summary>
public interface IDriveClient
{
    /// <summary>Bootstrap cursor: <c>changes.getStartPageToken</c>.</summary>
    Task<string> GetStartPageTokenAsync(CancellationToken ct);

    /// <summary>Fetch one page of changes from <paramref name="pageToken"/> (<c>changes.list</c>).</summary>
    Task<ChangePage> ListChangesAsync(string pageToken, CancellationToken ct);

    /// <summary>
    /// List EVERY file currently in <paramref name="folderId"/> as non-removed
    /// <see cref="DriveChange"/> entries (same shape <c>ProcessChangeAsync</c>
    /// consumes) — the one-time startup BACKFILL of the pre-existing recording
    /// backlog, independent of the changes cursor (which only ever sees files that
    /// mutate AFTER bootstrap). Returns the full folder snapshot; the caller runs
    /// each entry through the identical download→classify→pair→register path, so an
    /// already-imported recording is an idempotent no-op.
    /// </summary>
    Task<IReadOnlyList<DriveChange>> ListFolderAsync(string folderId, CancellationToken ct);

    /// <summary>Read one file's metadata (fields WATCH needs), or null if gone.</summary>
    Task<DriveChange?> GetMetadataAsync(string fileId, CancellationToken ct);

    /// <summary>Stream the file media (<c>alt=media</c>) into <paramref name="destination"/>.</summary>
    Task DownloadMediaAsync(string fileId, Stream destination, CancellationToken ct);
}
