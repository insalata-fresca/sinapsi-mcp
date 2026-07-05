using Cervello.Watcher.Domain;
using Cervello.Watcher.Drive;

namespace Cervello.Watcher.Tests;

/// <summary>
/// In-memory <see cref="IDriveClient"/> for all core tests. Seed files (metadata +
/// bytes) and a scripted changes feed; drive the whole WATCH → NORMALIZE cycle
/// with no Google. Supports injecting a download fault to exercise error
/// classification.
/// </summary>
public sealed class FakeDriveClient : IDriveClient
{
    private readonly Dictionary<string, DriveChange> _meta = new();
    private readonly Dictionary<string, byte[]> _bytes = new();
    private readonly List<ChangePage> _pages = new();
    private int _pageCursor;
    private string _startToken = "start-0";

    /// <summary>fileId -> a fault to throw on download (else null).</summary>
    public Dictionary<string, Exception> DownloadFaults { get; } = new();

    public int DownloadCallCount { get; private set; }
    public List<string> DownloadedFileIds { get; } = new();

    public void SetStartToken(string t) => _startToken = t;

    /// <summary>Seed a file: metadata + optional bytes.</summary>
    public DriveChange SeedFile(
        string fileId,
        string name,
        string mimeType,
        byte[]? bytes,
        string? md5 = null,
        DateTimeOffset? createdTime = null,
        IReadOnlyList<string>? parents = null,
        bool removed = false,
        bool trashed = false)
    {
        var change = new DriveChange(
            fileId: fileId,
            name: name,
            mimeType: mimeType,
            md5: md5 ?? (bytes is not null ? Convert.ToHexString(System.Security.Cryptography.MD5.HashData(bytes)).ToLowerInvariant() : null),
            size: bytes?.LongLength,
            createdTime: createdTime ?? DateTimeOffset.Parse("2026-07-05T09:30:00Z"),
            modifiedTime: createdTime ?? DateTimeOffset.Parse("2026-07-05T09:30:00Z"),
            parents: parents,
            removed: removed,
            trashed: trashed);
        _meta[fileId] = change;
        if (bytes is not null) _bytes[fileId] = bytes;
        return change;
    }

    /// <summary>Queue one page of changes to be returned by successive ListChangesAsync calls.</summary>
    public void QueuePage(IEnumerable<DriveChange> changes, string? nextPageToken = null, string? newStartPageToken = "next-token")
        => _pages.Add(new ChangePage(changes.ToList(), nextPageToken, newStartPageToken));

    public Task<string> GetStartPageTokenAsync(CancellationToken ct) => Task.FromResult(_startToken);

    public Task<ChangePage> ListChangesAsync(string pageToken, CancellationToken ct)
    {
        if (_pageCursor < _pages.Count)
            return Task.FromResult(_pages[_pageCursor++]);
        // No more scripted pages: an empty page that does not advance the cursor.
        return Task.FromResult(new ChangePage(Array.Empty<DriveChange>(), null, pageToken));
    }

    public Task<DriveChange?> GetMetadataAsync(string fileId, CancellationToken ct) =>
        Task.FromResult(_meta.TryGetValue(fileId, out var m) ? m : null);

    /// <summary>Synchronous metadata accessor for tests (no blocking .Result on a Task).</summary>
    public DriveChange Meta(string fileId) => _meta[fileId];

    public Task DownloadMediaAsync(string fileId, Stream destination, CancellationToken ct)
    {
        DownloadCallCount++;
        DownloadedFileIds.Add(fileId);
        if (DownloadFaults.TryGetValue(fileId, out var fault))
            throw fault;
        if (!_bytes.TryGetValue(fileId, out var b))
            throw new DriveMediaException($"404 {fileId}", transient: false);
        destination.Write(b, 0, b.Length);
        return Task.CompletedTask;
    }
}
