using System.Text.Json;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// Live CT146 Postgres <see cref="IOpenPointStore"/> (DESIGN §6.1; design Data Model →
/// <c>open_points</c>). The apply stage ENQUEUES an escalated fact here (idempotent on
/// <c>point_id</c>) rather than writing it to <c>map/</c>; the cervello MCP lists + answers them.
/// Resolution is single-shot (the double-apply guard): a resolved point cannot be re-resolved.
/// Mirrors the <see cref="InMemoryOpenPointStore"/> contract exactly. Never git.
///
/// <para>Candidates + the merged-speaker label are stored REDACTED (scored candidate values +
/// rationale only — R10: no bodies / audio / vectors). L2 verification deferred (like
/// PostgresStateStore): compiles + DI-registered; SQL/DDL asserted by review + the opt-in offline
/// integration test; LIVE behaviour is an L2 step.</para>
/// </summary>
public sealed class PgOpenPointStore : IOpenPointStore
{
    private const string Ddl = """
        CREATE TABLE IF NOT EXISTS open_points (
            point_id           TEXT PRIMARY KEY,
            kind               TEXT NOT NULL,
            recording_id       TEXT NOT NULL,
            bundle_id          TEXT NOT NULL,
            question_redacted  TEXT NOT NULL,
            candidates         JSONB NOT NULL DEFAULT '[]'::jsonb,
            merged_speaker     TEXT,
            status             TEXT NOT NULL DEFAULT 'pending',
            resolved_answer_id TEXT,
            resolved_value     TEXT,
            resolved_basis_id  TEXT,
            resolved_dismissed BOOLEAN,
            created_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
            resolved_at        TIMESTAMPTZ
        );
        CREATE INDEX IF NOT EXISTS idx_open_points_pending
            ON open_points (recording_id) WHERE status = 'pending';
        """;

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private readonly string _connString;
    private readonly ILogger _log;

    public PgOpenPointStore(EnrichmentConfig cfg, ILogger<PgOpenPointStore>? log = null)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        _connString = cfg.PostgresDsn;
        _log = log ?? NullLogger<PgOpenPointStore>.Instance;
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
                _log.LogWarning(e, "open-points schema-ensure attempt {Attempt}/{Max} failed; retry in {Delay}s",
                    attempt, maxAttempts, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
        }
    }

    public async Task<bool> EnqueueAsync(OpenPoint point, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(point);
        await using var c = await OpenAsync(ct);
        // Idempotent on point_id: a re-run of apply re-enqueues the same point as a no-op.
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO open_points
                (point_id, kind, recording_id, bundle_id, question_redacted, candidates, merged_speaker, status, created_at)
            VALUES (@id, @kind, @rec, @bundle, @q, @cand::jsonb, @spk, 'pending', now())
            ON CONFLICT (point_id) DO NOTHING
            """, c);
        cmd.Parameters.AddWithValue("id", point.PointId);
        cmd.Parameters.AddWithValue("kind", point.Kind.ToString());
        cmd.Parameters.AddWithValue("rec", point.RecordingId);
        cmd.Parameters.AddWithValue("bundle", point.BundleId);
        cmd.Parameters.AddWithValue("q", point.QuestionRedacted);
        cmd.Parameters.AddWithValue("cand", SerializeCandidates(point.Candidates));
        cmd.Parameters.AddWithValue("spk", (object?)point.MergedSpeaker ?? DBNull.Value);
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows > 0;
    }

    public async Task<IReadOnlyList<OpenPoint>> ListPendingAsync(string? recordingId = null, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var sql = """
            SELECT point_id, kind, recording_id, bundle_id, question_redacted, candidates, merged_speaker
            FROM open_points WHERE status = 'pending'
            """;
        if (recordingId is not null) sql += " AND recording_id = @rec";
        await using var cmd = new NpgsqlCommand(sql, c);
        if (recordingId is not null) cmd.Parameters.AddWithValue("rec", recordingId);
        var outp = new List<OpenPoint>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            outp.Add(ReadPoint(r));
        return outp;
    }

    public async Task<OpenPoint?> GetAsync(string pointId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pointId)) return null;
        await using var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT point_id, kind, recording_id, bundle_id, question_redacted, candidates, merged_speaker
            FROM open_points WHERE point_id = @id
            """, c);
        cmd.Parameters.AddWithValue("id", pointId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadPoint(r) : null;
    }

    public async Task<bool> ResolveAsync(string pointId, OpenPointResolution resolution, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        await using var c = await OpenAsync(ct);
        // Single-shot: the WHERE status='pending' clause is the double-apply guard — a second resolve
        // affects 0 rows and returns false, exactly like the in-memory store.
        await using var cmd = new NpgsqlCommand("""
            UPDATE open_points SET
                status = 'resolved', resolved_answer_id = @ans, resolved_value = @val,
                resolved_basis_id = @basis, resolved_dismissed = @dismissed, resolved_at = now()
            WHERE point_id = @id AND status = 'pending'
            """, c);
        cmd.Parameters.AddWithValue("id", pointId);
        cmd.Parameters.AddWithValue("ans", resolution.AnswerId);
        cmd.Parameters.AddWithValue("val", (object?)resolution.ConfirmedValue ?? DBNull.Value);
        cmd.Parameters.AddWithValue("basis", (object?)resolution.BasisId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("dismissed", resolution.Dismissed);
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows > 0;
    }

    public async Task<bool> IsResolvedAsync(string pointId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pointId)) return false;
        await using var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT 1 FROM open_points WHERE point_id = @id AND status = 'resolved'", c);
        cmd.Parameters.AddWithValue("id", pointId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static OpenPoint ReadPoint(NpgsqlDataReader r)
    {
        var kind = Enum.TryParse<OpenPointKind>(r.GetString(1), ignoreCase: true, out var k) ? k : OpenPointKind.Fact;
        var candidates = DeserializeCandidates(r.IsDBNull(5) ? "[]" : r.GetString(5));
        var mergedSpeaker = r.IsDBNull(6) ? null : r.GetString(6);
        return new OpenPoint(r.GetString(0), kind, r.GetString(2), r.GetString(3), r.GetString(4), candidates, mergedSpeaker);
    }

    private static string SerializeCandidates(IReadOnlyList<ScoredCandidate> cands) =>
        JsonSerializer.Serialize(cands.Select(c => new WireCandidate(c.Value, c.Confidence, c.Why)), _json);

    private static IReadOnlyList<ScoredCandidate> DeserializeCandidates(string json)
    {
        var wire = JsonSerializer.Deserialize<List<WireCandidate>>(json, _json) ?? [];
        return wire.Select(w => new ScoredCandidate(w.Value, w.Confidence, w.Why)).ToList();
    }

    private sealed record WireCandidate(string Value, double Confidence, string Why);
}
