using Cervello.Watcher.Domain;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Cervello.Watcher.State;

/// <summary>
/// On-CT Postgres <see cref="IStateStore"/> (D4). Three tables:
/// <c>watcher_cursor</c>, <c>watcher_download</c>, <c>watcher_recording</c>.
/// Schema-ensure is retried on startup (mirrors IndexerCore.EnsureSchemaWithRetryAsync)
/// so a race with Postgres coming up does not crash the boot. Compiles + is
/// DI-registered; its LIVE behaviour is a gated integration test (real DB), out of
/// scope for this session's suite — the cursor/idempotency discipline is proven
/// against InMemoryStateStore.
/// </summary>
public sealed class PostgresStateStore : IStateStore
{
    private const string Ddl = """
        CREATE TABLE IF NOT EXISTS watcher_cursor (
            scope       TEXT PRIMARY KEY,
            page_token  TEXT NOT NULL,
            updated_at  TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        CREATE TABLE IF NOT EXISTS watcher_download (
            drive_key   TEXT PRIMARY KEY,
            file_id     TEXT NOT NULL,
            md5         TEXT,
            kind        TEXT NOT NULL,
            staged_path TEXT,
            sha256      TEXT,
            state       TEXT NOT NULL,
            reason      TEXT,
            updated_at  TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        CREATE TABLE IF NOT EXISTS watcher_recording (
            recording_id  TEXT NOT NULL,
            recording_key TEXT PRIMARY KEY,
            basename      TEXT NOT NULL,
            audio_sha256  TEXT NOT NULL,
            audio_drive_id TEXT NOT NULL,
            txt_drive_id  TEXT,
            recorded_at   TEXT NOT NULL,
            state         TEXT NOT NULL,
            ready_at      TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        -- MIXED-cases: a transcript-only recording carries an empty audio_sha256 (the NOT NULL columns
        -- accept "") and is keyed by the drain on its transcript sha. Add the column idempotently so an
        -- EXISTING live table (created before this change) is migrated in place; ADD COLUMN IF NOT
        -- EXISTS is a no-op when the column is already present.
        ALTER TABLE watcher_recording ADD COLUMN IF NOT EXISTS transcript_sha256 TEXT;
        """;

    private readonly string _connString;
    private readonly ILogger _log;

    public PostgresStateStore(WatcherConfig cfg, ILogger<PostgresStateStore> log)
    {
        _connString = cfg.PostgresDsn;
        _log = log;
    }

    private async ValueTask<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var c = new NpgsqlConnection(_connString);
        await c.OpenAsync(ct);
        return c;
    }

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        const int maxAttempts = 10;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var c = await OpenAsync(ct);
                await using var cmd = new NpgsqlCommand(Ddl, c);
                await cmd.ExecuteNonQueryAsync(ct);
                return;
            }
            catch (Exception e) when (attempt < maxAttempts && !ct.IsCancellationRequested)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(30, attempt * 2));
                _log.LogWarning(e, "watcher schema-ensure attempt {Attempt}/{Max} failed; retrying in {Delay}s",
                    attempt, maxAttempts, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
        }
    }

    public async Task<string?> GetCursorAsync(string scope, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT page_token FROM watcher_cursor WHERE scope = @s", c);
        cmd.Parameters.AddWithValue("s", scope);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v as string;
    }

    public async Task SetCursorAsync(string scope, string pageToken, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO watcher_cursor (scope, page_token, updated_at)
            VALUES (@s, @t, now())
            ON CONFLICT (scope) DO UPDATE SET page_token = EXCLUDED.page_token, updated_at = now()
            """, c);
        cmd.Parameters.AddWithValue("s", scope);
        cmd.Parameters.AddWithValue("t", pageToken);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<DownloadRecord?> GetDownloadAsync(string driveKey, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT drive_key, file_id, md5, kind, staged_path, sha256, state, reason
            FROM watcher_download WHERE drive_key = @k
            """, c);
        cmd.Parameters.AddWithValue("k", driveKey);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            return null;
        return new DownloadRecord(
            r.GetString(0),
            r.GetString(1),
            r.IsDBNull(2) ? null : r.GetString(2),
            r.GetString(3),
            r.IsDBNull(4) ? null : r.GetString(4),
            r.IsDBNull(5) ? null : r.GetString(5),
            PipelineStateWire.Parse(r.GetString(6)), // tolerant: §5 wire or legacy PascalCase (E4)
            r.IsDBNull(7) ? null : r.GetString(7));
    }

    public async Task UpsertDownloadAsync(DownloadRecord record, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO watcher_download
                (drive_key, file_id, md5, kind, staged_path, sha256, state, reason, updated_at)
            VALUES (@k, @f, @m, @kind, @path, @sha, @state, @reason, now())
            ON CONFLICT (drive_key) DO UPDATE SET
                file_id = EXCLUDED.file_id, md5 = EXCLUDED.md5, kind = EXCLUDED.kind,
                staged_path = EXCLUDED.staged_path, sha256 = EXCLUDED.sha256,
                state = EXCLUDED.state, reason = EXCLUDED.reason, updated_at = now()
            """, c);
        cmd.Parameters.AddWithValue("k", record.DriveKey);
        cmd.Parameters.AddWithValue("f", record.FileId);
        cmd.Parameters.AddWithValue("m", (object?)record.Md5 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("kind", record.Kind);
        cmd.Parameters.AddWithValue("path", (object?)record.StagedPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("sha", (object?)record.Sha256 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("state", record.State.ToWire()); // SCHEMAS §5 wire name (E4)
        cmd.Parameters.AddWithValue("reason", (object?)record.Reason ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> RecordingExistsAsync(string recordingKey, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT 1 FROM watcher_recording WHERE recording_key = @k", c);
        cmd.Parameters.AddWithValue("k", recordingKey);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task UpsertRecordingAsync(Recording recording, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO watcher_recording
                (recording_id, recording_key, basename, audio_sha256, audio_drive_id,
                 txt_drive_id, transcript_sha256, recorded_at, state, ready_at)
            VALUES (@id, @key, @base, @sha, @adid, @tdid, @tsha, @rec, @state, now())
            ON CONFLICT (recording_key) DO NOTHING
            """, c);
        cmd.Parameters.AddWithValue("id", recording.Id);
        cmd.Parameters.AddWithValue("key", recording.RecordingKey);
        cmd.Parameters.AddWithValue("base", recording.Basename);
        cmd.Parameters.AddWithValue("sha", recording.AudioSha256);       // "" for transcript-only
        cmd.Parameters.AddWithValue("adid", recording.AudioDriveId);     // "" for transcript-only
        cmd.Parameters.AddWithValue("tdid", (object?)recording.TxtDriveId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("tsha", (object?)recording.TranscriptSha256 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("rec", recording.RecordedAt);
        cmd.Parameters.AddWithValue("state", recording.State.ToWire()); // SCHEMAS §5 wire name (E4)
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
