using Cervello.Watcher.Domain;
using Google.Apis.Download;
using Google.Apis.Drive.v3;

namespace Cervello.Watcher.Drive;

/// <summary>
/// The real <see cref="IDriveClient"/> over Google.Apis.Drive.v3. Read-only (SA +
/// DriveReadonly scope, D1) and proxy-routed (D2, wired in DriveClientFactory).
/// Compiles and is DI-registered, but its LIVE behaviour is out of scope for this
/// session's suite (gated on the operator's SA + folder share, Q1) — all core
/// tests drive FakeDriveClient instead.
/// </summary>
public sealed class GoogleDriveClient : IDriveClient
{
    private const string MetaFields =
        "id,name,mimeType,md5Checksum,size,createdTime,modifiedTime,parents,trashed";

    private readonly DriveService _drive;

    public GoogleDriveClient(DriveService drive) => _drive = drive;

    public async Task<string> GetStartPageTokenAsync(CancellationToken ct)
    {
        var req = _drive.Changes.GetStartPageToken();
        req.SupportsAllDrives = true;
        var resp = await req.ExecuteAsync(ct);
        return resp.StartPageTokenValue
            ?? throw new InvalidOperationException("Drive returned no start page token");
    }

    public async Task<ChangePage> ListChangesAsync(string pageToken, CancellationToken ct)
    {
        var req = _drive.Changes.List(pageToken);
        req.SupportsAllDrives = true;
        req.IncludeRemoved = true;
        req.Fields =
            "newStartPageToken,nextPageToken,changes(removed,fileId,file(" + MetaFields + "))";
        var resp = await req.ExecuteAsync(ct);

        var changes = new List<DriveChange>();
        foreach (var c in resp.Changes ?? new List<Google.Apis.Drive.v3.Data.Change>())
        {
            var f = c.File;
            var fileId = c.FileId ?? f?.Id;
            if (string.IsNullOrWhiteSpace(fileId))
                continue;
            changes.Add(new DriveChange(
                fileId: fileId,
                name: f?.Name,
                mimeType: f?.MimeType,
                md5: f?.Md5Checksum,
                size: f?.Size,
                createdTime: f?.CreatedTimeDateTimeOffset,
                modifiedTime: f?.ModifiedTimeDateTimeOffset,
                parents: f?.Parents?.ToList(),
                removed: c.Removed ?? false,
                trashed: f?.Trashed ?? false));
        }

        return new ChangePage(changes, resp.NextPageToken, resp.NewStartPageToken);
    }

    public async Task<DriveChange?> GetMetadataAsync(string fileId, CancellationToken ct)
    {
        var req = _drive.Files.Get(fileId);
        req.SupportsAllDrives = true;
        req.Fields = MetaFields;
        var f = await req.ExecuteAsync(ct);
        if (f is null)
            return null;
        return new DriveChange(
            fileId: f.Id,
            name: f.Name,
            mimeType: f.MimeType,
            md5: f.Md5Checksum,
            size: f.Size,
            createdTime: f.CreatedTimeDateTimeOffset,
            modifiedTime: f.ModifiedTimeDateTimeOffset,
            parents: f.Parents?.ToList(),
            removed: false,
            trashed: f.Trashed ?? false);
    }

    public async Task DownloadMediaAsync(string fileId, Stream destination, CancellationToken ct)
    {
        var req = _drive.Files.Get(fileId);
        req.SupportsAllDrives = true;
        var progress = await req.DownloadAsync(destination, ct);
        if (progress.Status == DownloadStatus.Failed)
            throw progress.Exception
                ?? new InvalidOperationException($"Drive media download failed for {fileId}");
    }
}
