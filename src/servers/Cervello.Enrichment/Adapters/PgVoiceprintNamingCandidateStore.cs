using System.Globalization;
using System.Text.Json;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// Live CT146 pgvector <see cref="IVoiceprintNamingCandidateStore"/> (design doc <c>ste/cervello</c>
/// <c>docs/design/voiceprint-naming.md</c> §7 phase V4, §4.4, table
/// <c>voiceprint_naming_candidates</c>). Mirrors <see cref="PgRecordingVoiceprintStore"/>'s pgvector
/// convention (text literal <c>'[f1,f2,…]'</c> cast <c>::vector</c>, no Pgvector NuGet package) and
/// <see cref="ISchemaInitializer"/> pattern.
///
/// <para>Source members (the contributing <c>(recording_id, cluster_index)</c> rows) are stored as
/// JSONB — a small, opaque, non-biometric list the naming surface never queries by field, only reads
/// back whole. Simpler than a sibling table for a handful of rows per candidate.</para>
/// </summary>
public sealed class PgVoiceprintNamingCandidateStore : IVoiceprintNamingCandidateStore, ISchemaInitializer
{
    public string SchemaName => "voiceprint_naming_candidates";

    private const string Ddl = """
        CREATE EXTENSION IF NOT EXISTS vector;
        CREATE TABLE IF NOT EXISTS voiceprint_naming_candidates (
            drive_file_id   TEXT PRIMARY KEY,
            sample_name     TEXT NOT NULL,
            centroid        vector(256) NOT NULL,
            source_members  JSONB NOT NULL,
            created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
            resolved        BOOLEAN NOT NULL DEFAULT false
        );
        CREATE INDEX IF NOT EXISTS idx_voiceprint_naming_candidates_unresolved
            ON voiceprint_naming_candidates (resolved);
        """;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly string _connString;
    private readonly ILogger _log;

    public PgVoiceprintNamingCandidateStore(EnrichmentConfig cfg, ILogger<PgVoiceprintNamingCandidateStore>? log = null)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        _connString = cfg.PostgresDsn;
        _log = log ?? NullLogger<PgVoiceprintNamingCandidateStore>.Instance;
    }

    private async ValueTask<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var c = new NpgsqlConnection(_connString);
        await c.OpenAsync(ct).ConfigureAwait(false);
        return c;
    }

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
                _log.LogWarning(e, "voiceprint-naming-candidate schema-ensure attempt {Attempt}/{Max} failed; retrying in {Delay}s",
                    attempt, maxAttempts, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
        }
    }

    public async Task<IReadOnlyList<string>> ReplaceUnresolvedAsync(
        IReadOnlyList<VoiceprintNamingCandidate> candidates, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        await using var c = await OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);

        var deleted = new List<string>();
        await using (var del = new NpgsqlCommand(
            "DELETE FROM voiceprint_naming_candidates WHERE resolved = false RETURNING drive_file_id", c, tx))
        await using (var r = await del.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
                deleted.Add(r.GetString(0));
        }

        foreach (var cand in candidates)
        {
            await using var ins = new NpgsqlCommand("""
                INSERT INTO voiceprint_naming_candidates
                    (drive_file_id, sample_name, centroid, source_members, created_at, resolved)
                VALUES (@id, @name, @v::vector, @members::jsonb, @created, @resolved)
                ON CONFLICT (drive_file_id) DO UPDATE SET
                    sample_name = EXCLUDED.sample_name,
                    centroid = EXCLUDED.centroid,
                    source_members = EXCLUDED.source_members,
                    created_at = EXCLUDED.created_at,
                    resolved = EXCLUDED.resolved
                """, c, tx);
            ins.Parameters.AddWithValue("id", cand.DriveFileId);
            ins.Parameters.AddWithValue("name", cand.SampleName);
            ins.Parameters.AddWithValue("v", VecLiteral(cand.Centroid));
            ins.Parameters.AddWithValue("members", SerializeMembers(cand.SourceMembers));
            ins.Parameters.AddWithValue("created", cand.CreatedAt);
            ins.Parameters.AddWithValue("resolved", cand.Resolved);
            await ins.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return deleted;
    }

    public async Task<VoiceprintNamingCandidate?> GetByDriveFileIdAsync(string driveFileId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(driveFileId)) return null;
        await using var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT drive_file_id, sample_name, centroid::text, source_members::text, created_at, resolved
            FROM voiceprint_naming_candidates
            WHERE drive_file_id = @id
            """, c);
        cmd.Parameters.AddWithValue("id", driveFileId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadRow(r) : null;
    }

    public async Task<IReadOnlyList<VoiceprintNamingCandidate>> GetUnresolvedAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT drive_file_id, sample_name, centroid::text, source_members::text, created_at, resolved
            FROM voiceprint_naming_candidates
            WHERE resolved = false
            ORDER BY sample_name ASC
            """, c);
        var rows = new List<VoiceprintNamingCandidate>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add(ReadRow(r));
        return rows;
    }

    public async Task<bool> MarkResolvedAsync(string driveFileId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(driveFileId)) return false;
        await using var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            UPDATE voiceprint_naming_candidates SET resolved = true
            WHERE drive_file_id = @id AND resolved = false
            """, c);
        cmd.Parameters.AddWithValue("id", driveFileId);
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows > 0;
    }

    private static VoiceprintNamingCandidate ReadRow(NpgsqlDataReader r) => new(
        sampleName: r.GetString(1),
        driveFileId: r.GetString(0),
        centroid: ParseVecLiteral(r.GetString(2)),
        sourceMembers: DeserializeMembers(r.GetString(3)),
        createdAt: r.GetFieldValue<DateTimeOffset>(4),
        resolved: r.GetBoolean(5));

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

    private sealed record MemberDto(string RecordingId, int ClusterIndex, double DurationSeconds, int SegmentCount);

    private static string SerializeMembers(IReadOnlyList<VoiceReviewMember> members) =>
        JsonSerializer.Serialize(
            members.Select(m => new MemberDto(m.RecordingId, m.ClusterIndex, m.DurationSeconds, m.SegmentCount)),
            JsonOpts);

    private static IReadOnlyList<VoiceReviewMember> DeserializeMembers(string json)
    {
        var dtos = JsonSerializer.Deserialize<List<MemberDto>>(json, JsonOpts) ?? [];
        return dtos.Select(d => new VoiceReviewMember(d.RecordingId, d.ClusterIndex, d.DurationSeconds, d.SegmentCount)).ToList();
    }
}
