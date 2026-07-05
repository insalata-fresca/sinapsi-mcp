using Cervello.Watcher.Domain;
using Cervello.Watcher.Drive;
using Cervello.Watcher.Normalize;

namespace Cervello.Watcher.Tests;

/// <summary>Wraps a real IDriveClient and records the pageToken passed to ListChangesAsync.</summary>
public sealed class RecordingDriveSpy : IDriveClient
{
    private readonly IDriveClient _inner;
    private readonly Action<string> _onList;

    public RecordingDriveSpy(IDriveClient inner, Action<string> onList)
    {
        _inner = inner;
        _onList = onList;
    }

    public Task<string> GetStartPageTokenAsync(CancellationToken ct) => _inner.GetStartPageTokenAsync(ct);

    public Task<ChangePage> ListChangesAsync(string pageToken, CancellationToken ct)
    {
        _onList(pageToken);
        return _inner.ListChangesAsync(pageToken, ct);
    }

    public Task<DriveChange?> GetMetadataAsync(string fileId, CancellationToken ct) =>
        _inner.GetMetadataAsync(fileId, ct);

    public Task DownloadMediaAsync(string fileId, Stream destination, CancellationToken ct) =>
        _inner.DownloadMediaAsync(fileId, destination, ct);
}

/// <summary>An IManifestStore that always throws — models a batch-processing failure.</summary>
public sealed class ThrowingManifestStore : IManifestStore
{
    public Task<bool> AppendAsync(ManifestEntry entry, CancellationToken ct) =>
        throw new InvalidOperationException("manifest write failed (injected)");
}
