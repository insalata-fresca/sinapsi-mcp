namespace Cervello.Watcher.Drive;

/// <summary>
/// A Drive-media-fetch failure from an <see cref="IDriveClient"/> implementation
/// (M6-refine: replaces classifying <c>Google.GoogleApiException</c> by HTTP
/// status, now that Drive access is gdrive-MCP tool calls rather than a direct
/// Google.Apis.Drive.v3 client — there is no Google exception type to catch
/// anymore). <see cref="Transient"/> is set by the THROWING client (it knows
/// whether the underlying failure is worth retrying under the same idempotency
/// key), and <see cref="Downloader.IsTransient"/> trusts it directly instead of
/// re-deriving it from a status code.
/// </summary>
public sealed class DriveMediaException(string reason, bool transient)
    : Exception(reason)
{
    public bool Transient { get; } = transient;
}
