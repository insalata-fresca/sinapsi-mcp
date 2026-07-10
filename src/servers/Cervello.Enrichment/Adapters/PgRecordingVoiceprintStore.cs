using System.Globalization;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// Live CT146 pgvector <see cref="IRecordingVoiceprintStore"/> (design doc <c>ste/cervello</c>
/// <c>docs/design/autonomous-attribution.md</c> §4.1/§5 M3, table <c>recording_voiceprints</c>). This
/// is the RAW CORPUS — every recording's per-speaker merged-cluster centroids, durably accumulated —
/// distinct from <see cref="PgVoiceprintStore"/> (the confirmed, enrolled-person store; unchanged by
/// this mission). Mirrors the <see cref="InMemoryRecordingVoiceprintStore"/> contract exactly so the
/// two are drop-in behind the port.
///
/// <para><b>Key = (recording_id, cluster_index), never a diarizer label.</b> The primary key is the
/// recording's merged-cluster POSITION — never <c>MergedCluster.MergedSpeaker</c> (the sidecar's
/// <c>s1…</c> label), because those labels are per-recording only and are NOT stable across
/// recordings. Persisting the same recording again (idempotency-key replay / re-process) UPSERTS by
/// this key — it never duplicates rows.</para>
///
/// <para><b>pgvector convention (from Sinapsi.Indexer, same as <see cref="PgVoiceprintStore"/>):</b>
/// the 256-d centroid is written as a text literal <c>'[f1,f2,…]'</c> (invariant culture) and cast
/// <c>::vector</c> in SQL; the HNSW cosine index is provisioned for the M4 corpus-wide cross-recording
/// clusterer to query. No Pgvector NuGet package — Npgsql + the text literal.</para>
///
/// <para><b>L2 verification (deferred, like the sibling Pg stores):</b> this adapter COMPILES + is
/// DI-registered; its SQL/DDL is asserted by review + an opt-in offline pgvector integration test
/// (podman-backed, skipped by default). LIVE behaviour against the real CT146 DB is an L2 on-CT
/// integration step.</para>
/// </summary>
public sealed class PgRecordingVoiceprintStore : IRecordingVoiceprintStore, ISchemaInitializer
{
    public string SchemaName => "recording_voiceprints, recording_voiceprint_segments";

    private const string Ddl = """
        CREATE EXTENSION IF NOT EXISTS vector;
        CREATE TABLE IF NOT EXISTS recording_voiceprints (
            recording_id    TEXT NOT NULL,
            cluster_index   INT NOT NULL,
            centroid        vector(256) NOT NULL,
            model           TEXT NOT NULL,
            segment_count   INT NOT NULL,
            duration_seconds DOUBLE PRECISION NOT NULL,
            merged_speaker_label TEXT NOT NULL,
            created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
            PRIMARY KEY (recording_id, cluster_index)
        );
        CREATE INDEX IF NOT EXISTS idx_recording_voiceprints_centroid
            ON recording_voiceprints USING hnsw (centroid vector_cosine_ops);
        CREATE TABLE IF NOT EXISTS recording_voiceprint_segments (
            recording_id    TEXT NOT NULL,
            cluster_index   INT NOT NULL,
            seg_order       INT NOT NULL,
            speaker         TEXT NOT NULL,
            start_seconds   DOUBLE PRECISION NOT NULL,
            end_seconds     DOUBLE PRECISION NOT NULL,
            PRIMARY KEY (recording_id, cluster_index, seg_order),
            FOREIGN KEY (recording_id, cluster_index)
                REFERENCES recording_voiceprints (recording_id, cluster_index)
                ON DELETE CASCADE
        );
        """;

    private readonly string _connString;
    private readonly ILogger _log;

    public PgRecordingVoiceprintStore(EnrichmentConfig cfg, ILogger<PgRecordingVoiceprintStore>? log = null)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        _connString = cfg.PostgresDsn;
        _log = log ?? NullLogger<PgRecordingVoiceprintStore>.Instance;
    }

    private async ValueTask<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var c = new NpgsqlConnection(_connString);
        await c.OpenAsync(ct).ConfigureAwait(false);
        return c;
    }

    /// <summary>Ensure the pgvector extension + table (retried on startup, mirrors PgVoiceprintStore).</summary>
    public async Task EnsureSchemaAsync(CancellationToken ct = default)
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
                var delay = TimeSpan.FromSeconds(System.Math.Min(30, attempt * 2));
                _log.LogWarning(e, "recording-voiceprint schema-ensure attempt {Attempt}/{Max} failed; retrying in {Delay}s",
                    attempt, maxAttempts, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
        }
    }

    public async Task PersistAsync(
        string recordingId, IReadOnlyList<RecordingVoiceprint> clusters, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(recordingId))
            throw new ArgumentException("recordingId must be non-empty", nameof(recordingId));
        ArgumentNullException.ThrowIfNull(clusters);
        if (clusters.Count == 0) return; // legal no-op — a recording with zero merged clusters

        await using var c = await OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);

        foreach (var row in clusters)
        {
            if (!string.Equals(row.RecordingId, recordingId, StringComparison.Ordinal))
                throw new ArgumentException(
                    $"cluster row RecordingId '{row.RecordingId}' does not match '{recordingId}'", nameof(clusters));

            await using var up = new NpgsqlCommand("""
                INSERT INTO recording_voiceprints
                    (recording_id, cluster_index, centroid, model, segment_count, duration_seconds,
                     merged_speaker_label, created_at)
                VALUES (@rec, @idx, @v::vector, @model, @segs, @dur, @label, @created)
                ON CONFLICT (recording_id, cluster_index) DO UPDATE SET
                    centroid = EXCLUDED.centroid,
                    model = EXCLUDED.model,
                    segment_count = EXCLUDED.segment_count,
                    duration_seconds = EXCLUDED.duration_seconds,
                    merged_speaker_label = EXCLUDED.merged_speaker_label,
                    created_at = EXCLUDED.created_at
                """, c, tx);
            up.Parameters.AddWithValue("rec", row.RecordingId);
            up.Parameters.AddWithValue("idx", row.ClusterIndex);
            up.Parameters.AddWithValue("v", VecLiteral(row.Centroid));
            up.Parameters.AddWithValue("model", row.Model);
            up.Parameters.AddWithValue("segs", row.SegmentCount);
            up.Parameters.AddWithValue("dur", row.DurationSeconds);
            up.Parameters.AddWithValue("label", row.MergedSpeakerLabel);
            up.Parameters.AddWithValue("created", row.CreatedAt);
            await up.ExecuteNonQueryAsync(ct);

            // Replace this cluster's segment ranges wholesale (delete-then-insert) — simplest way to
            // stay idempotent on a re-process where the segment COUNT itself may change (a re-run of
            // diarize-embed need not yield the exact same segment boundaries), never leaving stale
            // rows from a prior persist behind. Same transaction as the parent upsert above — a
            // cluster's centroid and its segment ranges are never observably out of sync.
            await using (var del = new NpgsqlCommand("""
                DELETE FROM recording_voiceprint_segments WHERE recording_id = @rec AND cluster_index = @idx
                """, c, tx))
            {
                del.Parameters.AddWithValue("rec", row.RecordingId);
                del.Parameters.AddWithValue("idx", row.ClusterIndex);
                await del.ExecuteNonQueryAsync(ct);
            }

            for (var i = 0; i < row.Segments.Count; i++)
            {
                var seg = row.Segments[i];
                await using var segCmd = new NpgsqlCommand("""
                    INSERT INTO recording_voiceprint_segments
                        (recording_id, cluster_index, seg_order, speaker, start_seconds, end_seconds)
                    VALUES (@rec, @idx, @ord, @speaker, @start, @end)
                    """, c, tx);
                segCmd.Parameters.AddWithValue("rec", row.RecordingId);
                segCmd.Parameters.AddWithValue("idx", row.ClusterIndex);
                segCmd.Parameters.AddWithValue("ord", i);
                segCmd.Parameters.AddWithValue("speaker", seg.Speaker);
                segCmd.Parameters.AddWithValue("start", seg.Start);
                segCmd.Parameters.AddWithValue("end", seg.End);
                await segCmd.ExecuteNonQueryAsync(ct);
            }
        }

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<RecordingVoiceprint>> GetForRecordingAsync(
        string recordingId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(recordingId)) return Array.Empty<RecordingVoiceprint>();
        await using var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT recording_id, cluster_index, centroid::text, model, segment_count, duration_seconds,
                   merged_speaker_label, created_at
            FROM recording_voiceprints
            WHERE recording_id = @rec
            ORDER BY cluster_index ASC
            """, c);
        cmd.Parameters.AddWithValue("rec", recordingId);
        var rows = new List<RecordingVoiceprint>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
                rows.Add(ReadRow(r));
        }

        var segsByCluster = await LoadSegmentsAsync(c, "WHERE recording_id = @rec",
            cmd2 => cmd2.Parameters.AddWithValue("rec", recordingId), ct);
        return AttachSegments(rows, segsByCluster);
    }

    public async Task<IReadOnlyList<RecordingVoiceprint>> GetCorpusAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT recording_id, cluster_index, centroid::text, model, segment_count, duration_seconds,
                   merged_speaker_label, created_at
            FROM recording_voiceprints
            ORDER BY recording_id ASC, cluster_index ASC
            """, c);
        var rows = new List<RecordingVoiceprint>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
                rows.Add(ReadRow(r));
        }

        // Corpus-wide: one extra query for ALL segments (no per-row N+1), grouped in memory by
        // (recording_id, cluster_index) — the same identity key as everywhere else on this store.
        var segsByCluster = await LoadSegmentsAsync(c, sql: null, configure: null, ct);
        return AttachSegments(rows, segsByCluster);
    }

    public async Task<IReadOnlyList<DiarizedSegment>> GetSegmentsAsync(
        string recordingId, int clusterIndex, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(recordingId)) return Array.Empty<DiarizedSegment>();
        await using var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT speaker, start_seconds, end_seconds
            FROM recording_voiceprint_segments
            WHERE recording_id = @rec AND cluster_index = @idx
            ORDER BY seg_order ASC
            """, c);
        cmd.Parameters.AddWithValue("rec", recordingId);
        cmd.Parameters.AddWithValue("idx", clusterIndex);
        var segments = new List<DiarizedSegment>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            segments.Add(new DiarizedSegment(r.GetString(0), r.GetDouble(1), r.GetDouble(2)));
        return segments;
    }

    private static RecordingVoiceprint ReadRow(NpgsqlDataReader r) => new(
        recordingId: r.GetString(0),
        clusterIndex: r.GetInt32(1),
        centroid: ParseVecLiteral(r.GetString(2)),
        model: r.GetString(3),
        segmentCount: r.GetInt32(4),
        durationSeconds: r.GetDouble(5),
        mergedSpeakerLabel: r.GetString(6),
        createdAt: r.GetFieldValue<DateTimeOffset>(7));

    /// <summary>
    /// Batch-load segment ranges, optionally filtered by an extra WHERE clause, grouped by
    /// <c>(recording_id, cluster_index)</c> and ordered by <c>seg_order</c> within each group —
    /// avoids an N+1 query per <see cref="RecordingVoiceprint"/> row in <see cref="GetForRecordingAsync"/>
    /// / <see cref="GetCorpusAsync"/>.
    /// </summary>
    private static async Task<Dictionary<(string RecordingId, int ClusterIndex), List<DiarizedSegment>>> LoadSegmentsAsync(
        NpgsqlConnection c, string? sql, Action<NpgsqlCommand>? configure, CancellationToken ct)
    {
        var whereClause = sql is null ? "" : sql;
        await using var cmd = new NpgsqlCommand($"""
            SELECT recording_id, cluster_index, speaker, start_seconds, end_seconds
            FROM recording_voiceprint_segments
            {whereClause}
            ORDER BY recording_id ASC, cluster_index ASC, seg_order ASC
            """, c);
        configure?.Invoke(cmd);
        var result = new Dictionary<(string, int), List<DiarizedSegment>>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var key = (r.GetString(0), r.GetInt32(1));
            var seg = new DiarizedSegment(r.GetString(2), r.GetDouble(3), r.GetDouble(4));
            if (!result.TryGetValue(key, out var list))
                result[key] = list = new List<DiarizedSegment>();
            list.Add(seg);
        }
        return result;
    }

    private static List<RecordingVoiceprint> AttachSegments(
        List<RecordingVoiceprint> rows,
        Dictionary<(string RecordingId, int ClusterIndex), List<DiarizedSegment>> segsByCluster)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var segments = segsByCluster.TryGetValue((row.RecordingId, row.ClusterIndex), out var list)
                ? (IReadOnlyList<DiarizedSegment>)list
                : Array.Empty<DiarizedSegment>();
            rows[i] = new RecordingVoiceprint(
                row.RecordingId, row.ClusterIndex, row.Centroid, row.Model, row.SegmentCount,
                row.DurationSeconds, row.MergedSpeakerLabel, row.CreatedAt, segments);
        }
        return rows;
    }

    // pgvector text input/output: '[f1,f2,...]' (invariant culture), cast ::vector in SQL.
    private static string VecLiteral(IReadOnlyList<float> v) =>
        "[" + string.Join(",", v.Select(x => x.ToString("R", CultureInfo.InvariantCulture))) + "]";

    private static float[] ParseVecLiteral(string literal)
    {
        var trimmed = literal.Trim().Trim('[', ']');
        if (trimmed.Length == 0) return [];
        return trimmed.Split(',')
            .Select(s => float.Parse(s, CultureInfo.InvariantCulture))
            .ToArray();
    }
}
