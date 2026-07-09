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
        // Match the Postgres ON CONFLICT (recording_key) DO NOTHING semantics: a first insert wins,
        // a re-insert under the same key is a no-op (the upgrade path goes through UpgradeRecordingAsync).
        _recordings.TryAdd(recording.RecordingKey, recording);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Recording>> GetRecordingsByIdAsync(string recordingId, CancellationToken ct)
    {
        IReadOnlyList<Recording> rows = _recordings.Values
            .Where(r => r.Id == recordingId)
            .ToList();
        return Task.FromResult(rows);
    }

    public Task UpgradeRecordingAsync(string oldKey, Recording upgraded, CancellationToken ct)
    {
        // Replace the single-sided row (stored under oldKey) with the upgraded pair, keyed by its own
        // (possibly different) key. Remove-then-add so a transcript-only → pair upgrade (whose key
        // changes from rec:id:txt:sha to rec:id:audioSha) leaves exactly one row for the id.
        _recordings.TryRemove(oldKey, out _);
        _recordings[upgraded.RecordingKey] = upgraded;
        return Task.CompletedTask;
    }
}
