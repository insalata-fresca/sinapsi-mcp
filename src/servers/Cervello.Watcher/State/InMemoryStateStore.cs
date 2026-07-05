using System.Collections.Concurrent;
using Cervello.Watcher.Domain;

namespace Cervello.Watcher.State;

/// <summary>
/// In-memory <see cref="IStateStore"/> for tests: identical cursor + ledger
/// semantics to <see cref="PostgresStateStore"/>, no DB. Thread-safe.
/// </summary>
public sealed class InMemoryStateStore : IStateStore
{
    private readonly ConcurrentDictionary<string, string> _cursor = new();
    private readonly ConcurrentDictionary<string, DownloadRecord> _downloads = new();
    private readonly ConcurrentDictionary<string, Recording> _recordings = new();

    public Task EnsureSchemaAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<string?> GetCursorAsync(string scope, CancellationToken ct) =>
        Task.FromResult(_cursor.TryGetValue(scope, out var v) ? v : null);

    public Task SetCursorAsync(string scope, string pageToken, CancellationToken ct)
    {
        _cursor[scope] = pageToken;
        return Task.CompletedTask;
    }

    public Task<DownloadRecord?> GetDownloadAsync(string driveKey, CancellationToken ct) =>
        Task.FromResult(_downloads.TryGetValue(driveKey, out var v) ? v : null);

    public Task UpsertDownloadAsync(DownloadRecord record, CancellationToken ct)
    {
        _downloads[record.DriveKey] = record;
        return Task.CompletedTask;
    }

    public Task<bool> RecordingExistsAsync(string recordingKey, CancellationToken ct) =>
        Task.FromResult(_recordings.ContainsKey(recordingKey));

    public Task UpsertRecordingAsync(Recording recording, CancellationToken ct)
    {
        _recordings[recording.RecordingKey] = recording;
        return Task.CompletedTask;
    }
}
