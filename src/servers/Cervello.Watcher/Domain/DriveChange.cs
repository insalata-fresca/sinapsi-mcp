namespace Cervello.Watcher.Domain;

/// <summary>
/// One entry surfaced from the Drive Changes feed (or a metadata read), already
/// projected to the fields WATCH needs. Immutable; a bare <c>fileId</c> is invalid.
/// </summary>
public sealed record DriveChange
{
    public DriveChange(
        string fileId,
        string? name,
        string? mimeType,
        string? md5,
        long? size,
        DateTimeOffset? createdTime,
        DateTimeOffset? modifiedTime,
        IReadOnlyList<string>? parents,
        bool removed,
        bool trashed)
    {
        if (string.IsNullOrWhiteSpace(fileId))
            throw new ArgumentException("DriveChange.FileId must be non-empty", nameof(fileId));
        FileId = fileId;
        Name = name;
        MimeType = mimeType;
        Md5 = md5;
        Size = size;
        CreatedTime = createdTime;
        ModifiedTime = modifiedTime;
        Parents = parents ?? Array.Empty<string>();
        Removed = removed;
        Trashed = trashed;
    }

    public string FileId { get; }
    public string? Name { get; }
    public string? MimeType { get; }
    public string? Md5 { get; }
    public long? Size { get; }
    public DateTimeOffset? CreatedTime { get; }
    public DateTimeOffset? ModifiedTime { get; }
    public IReadOnlyList<string> Parents { get; }
    public bool Removed { get; }
    public bool Trashed { get; }

    /// <summary>The idempotency key for the download ledger: <c>drive:&lt;fileId&gt;:&lt;md5&gt;</c>.</summary>
    public string DriveKey => $"drive:{FileId}:{Md5 ?? "-"}";
}
