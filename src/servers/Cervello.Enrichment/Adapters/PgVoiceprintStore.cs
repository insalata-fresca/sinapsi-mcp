using System.Globalization;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Math;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// Live CT146 pgvector <see cref="IVoiceprintStore"/> (spec <c>voiceprint-store</c>; design Data
/// Model → <c>voiceprints</c>). Enrolled centroids live ONLY here (pgvector, on CT146) — never git,
/// never a shared subject, never off-CT (invariant + DESIGN §10.4). Mirrors the
/// <see cref="InMemoryVoiceprintStore"/> contract EXACTLY so the two are drop-in behind the port:
/// running-centroid weighted refine, §10 allowlist hard-gate, OPERATIONS §7 deletion runbook +
/// tombstone (so lint R8 "no re-attribution after deletion" holds).
///
/// <para><b>pgvector convention (from Sinapsi.Indexer):</b> the 256-d centroid is written as a text
/// literal <c>'[f1,f2,…]'</c> (invariant culture) and cast <c>::vector</c> in SQL; matching uses the
/// cosine-distance operator <c>&lt;=&gt;</c> (cosine similarity = <c>1 - distance</c>). No Pgvector
/// NuGet package — Npgsql + the text literal, the established homelab pattern.</para>
///
/// <para><b>L2 verification (deferred, like the Watcher's <c>PostgresStateStore</c>):</b> this
/// adapter COMPILES + is DI-registered; its SQL/DDL is asserted by review + an opt-in offline
/// pgvector integration test (podman-backed, skipped by default). LIVE behaviour against the real
/// CT146 DB — extension present, HNSW index build, cosine parity with the in-memory store — is an
/// L2 on-CT integration step (see the mission return's L2 checklist).</para>
/// </summary>
public sealed class PgVoiceprintStore : IVoiceprintStore, ISchemaInitializer
{
    public string SchemaName => "voiceprints, voiceprint_tombstones, voiceprint_enrollment_audio";

    private const string Ddl = """
        CREATE EXTENSION IF NOT EXISTS vector;
        CREATE TABLE IF NOT EXISTS voiceprints (
            person_slug     TEXT PRIMARY KEY,
            centroid        vector(256) NOT NULL,
            sample_count    INT NOT NULL,
            enrolled_at     DATE NOT NULL,
            last_match      DOUBLE PRECISION,
            source_segments JSONB NOT NULL DEFAULT '[]'::jsonb,
            updated_at      TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        CREATE INDEX IF NOT EXISTS idx_voiceprints_centroid
            ON voiceprints USING hnsw (centroid vector_cosine_ops);
        -- Tombstones: a deleted person MUST NOT be re-attributed (lint R8) until a fresh
        -- operator-confirmed enrollment clears the tombstone (re-consent).
        CREATE TABLE IF NOT EXISTS voiceprint_tombstones (
            person_slug  TEXT PRIMARY KEY,
            deleted_at   TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        -- Enrollment audio segment refs, purged by the deletion runbook (never git-side).
        CREATE TABLE IF NOT EXISTS voiceprint_enrollment_audio (
            person_slug  TEXT NOT NULL,
            segment_ref  TEXT NOT NULL,
            PRIMARY KEY (person_slug, segment_ref)
        );
        """;

    private readonly string _connString;
    private readonly EnrollmentAllowlist _allowlist;
    private readonly IEnrollmentConsentStore? _consent;
    private readonly ILogger _log;

    public PgVoiceprintStore(
        EnrichmentConfig cfg,
        EnrollmentAllowlist allowlist,
        IEnrollmentConsentStore? consent = null,
        ILogger<PgVoiceprintStore>? log = null)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        _connString = cfg.PostgresDsn;
        _allowlist = allowlist ?? throw new ArgumentNullException(nameof(allowlist));
        // The durable, runtime-mutable §10 consent extension (V5 rename-consent); optional (null pre-V5).
        // When present its slugs UNION with the static allowlist so a rename-consented person enrolls
        // without a redeploy. Never REPLACES the static gate; only widens it.
        _consent = consent;
        _log = log ?? NullLogger<PgVoiceprintStore>.Instance;
    }

    private async ValueTask<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var c = new NpgsqlConnection(_connString);
        await c.OpenAsync(ct).ConfigureAwait(false);
        return c;
    }

    /// <summary>Ensure the pgvector extension + tables (retried on startup, mirrors PostgresStateStore).</summary>
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
                _log.LogWarning(e, "voiceprint schema-ensure attempt {Attempt}/{Max} failed; retrying in {Delay}s",
                    attempt, maxAttempts, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
        }
    }

    public async Task<IReadOnlyList<VoiceprintMatch>> MatchAsync(IReadOnlyList<float> centroid, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(centroid);
        // Cosine similarity = 1 - cosine distance (<=>). Ordered by descending similarity, then slug
        // (matches InMemoryVoiceprintStore's deterministic tie-break).
        await using var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT person_slug, 1 - (centroid <=> @v::vector) AS cosine
            FROM voiceprints
            ORDER BY cosine DESC, person_slug ASC
            """, c);
        cmd.Parameters.AddWithValue("v", VecLiteral(centroid));
        var matches = new List<VoiceprintMatch>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            matches.Add(new VoiceprintMatch(r.GetString(0), r.GetDouble(1)));
        return matches;
    }

    public async Task<Voiceprint?> GetAsync(string personSlug, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(personSlug)) return null;
        await using var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT person_slug, centroid::text, sample_count, enrolled_at, last_match, source_segments
            FROM voiceprints WHERE person_slug = @s
            """, c);
        cmd.Parameters.AddWithValue("s", personSlug);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return ReadVoiceprint(r);
    }

    public async Task<Voiceprint> EnrollOrRefineAsync(
        string personSlug,
        IReadOnlyList<float> confirmedCentroid,
        IReadOnlyList<string> sourceSegments,
        double? matchCosine,
        DateOnly on,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(personSlug))
            throw new ArgumentException("personSlug must be non-empty", nameof(personSlug));
        ArgumentNullException.ThrowIfNull(confirmedCentroid);
        ArgumentNullException.ThrowIfNull(sourceSegments);

        // §10 allowlist — hard gate (never a silent skip), identical to the in-memory store. The static
        // allowlist is UNIONed with the durable rename-consent store (V5): a person the operator
        // consented-to-enroll via a Drive rename passes the gate even if not in the deploy-time set.
        if (!_allowlist.IsAllowed(personSlug)
            && !(_consent is not null && await _consent.IsConsentedAsync(personSlug, ct).ConfigureAwait(false)))
            throw new EnrollmentNotAllowedException(personSlug);

        await using var c = await OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);

        // Re-consent: a fresh confirmed enrollment clears the deletion tombstone.
        await using (var clear = new NpgsqlCommand("DELETE FROM voiceprint_tombstones WHERE person_slug = @s", c, tx))
        {
            clear.Parameters.AddWithValue("s", personSlug);
            await clear.ExecuteNonQueryAsync(ct);
        }

        // Record enrollment audio segment refs (purged by the deletion runbook).
        foreach (var seg in sourceSegments)
        {
            await using var ins = new NpgsqlCommand("""
                INSERT INTO voiceprint_enrollment_audio (person_slug, segment_ref)
                VALUES (@s, @seg) ON CONFLICT DO NOTHING
                """, c, tx);
            ins.Parameters.AddWithValue("s", personSlug);
            ins.Parameters.AddWithValue("seg", seg);
            await ins.ExecuteNonQueryAsync(ct);
        }

        var existing = await GetInTxAsync(c, tx, personSlug, ct);
        Voiceprint updated;
        if (existing is not null)
        {
            // Refine: running centroid weighted by prior sample_count, sample_count++ (spec parity).
            var newCount = existing.SampleCount + 1;
            var refined = WeightedCentroid(existing.Centroid, existing.SampleCount, confirmedCentroid, 1);
            var segments = existing.SourceSegments.Concat(sourceSegments).ToList();
            updated = new Voiceprint(personSlug, refined, newCount, existing.EnrolledAt, matchCosine, segments);
        }
        else
        {
            if (confirmedCentroid.Count != SpeakerEmbedding.ExpectedDim)
                throw new ArgumentException(
                    $"confirmedCentroid must be {SpeakerEmbedding.ExpectedDim}-d", nameof(confirmedCentroid));
            updated = new Voiceprint(personSlug, confirmedCentroid, sampleCount: 1, on, matchCosine, sourceSegments);
        }

        await using (var up = new NpgsqlCommand("""
            INSERT INTO voiceprints (person_slug, centroid, sample_count, enrolled_at, last_match, source_segments, updated_at)
            VALUES (@s, @v::vector, @n, @enrolled, @lm, @segs::jsonb, now())
            ON CONFLICT (person_slug) DO UPDATE SET
                centroid = EXCLUDED.centroid, sample_count = EXCLUDED.sample_count,
                last_match = EXCLUDED.last_match, source_segments = EXCLUDED.source_segments, updated_at = now()
            """, c, tx))
        {
            up.Parameters.AddWithValue("s", updated.PersonSlug);
            up.Parameters.AddWithValue("v", VecLiteral(updated.Centroid));
            up.Parameters.AddWithValue("n", updated.SampleCount);
            up.Parameters.AddWithValue("enrolled", updated.EnrolledAt.ToDateTime(TimeOnly.MinValue));
            up.Parameters.AddWithValue("lm", (object?)updated.LastMatch ?? DBNull.Value);
            up.Parameters.AddWithValue("segs", System.Text.Json.JsonSerializer.Serialize(updated.SourceSegments));
            await up.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return updated;
    }

    public async Task<bool> DeleteAsync(string personSlug, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(personSlug))
            throw new ArgumentException("personSlug must be non-empty", nameof(personSlug));

        await using var c = await OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);

        int deleted;
        // OPERATIONS §7 step 1+2: delete centroid + enrollment rows, purge enrollment audio refs.
        await using (var del = new NpgsqlCommand("DELETE FROM voiceprints WHERE person_slug = @s", c, tx))
        {
            del.Parameters.AddWithValue("s", personSlug);
            deleted = await del.ExecuteNonQueryAsync(ct);
        }
        await using (var delAudio = new NpgsqlCommand("DELETE FROM voiceprint_enrollment_audio WHERE person_slug = @s", c, tx))
        {
            delAudio.Parameters.AddWithValue("s", personSlug);
            await delAudio.ExecuteNonQueryAsync(ct);
        }
        // OPERATIONS §7 step 4: tombstone so R8 blocks any new voice-match attribution.
        await using (var tomb = new NpgsqlCommand("""
            INSERT INTO voiceprint_tombstones (person_slug) VALUES (@s) ON CONFLICT DO NOTHING
            """, c, tx))
        {
            tomb.Parameters.AddWithValue("s", personSlug);
            await tomb.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return deleted > 0;
    }

    public async Task<bool> IsDeletedAsync(string personSlug, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(personSlug)) return false;
        await using var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT 1 FROM voiceprint_tombstones WHERE person_slug = @s", c);
        cmd.Parameters.AddWithValue("s", personSlug);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<Voiceprint?> GetInTxAsync(NpgsqlConnection c, NpgsqlTransaction tx, string slug, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT person_slug, centroid::text, sample_count, enrolled_at, last_match, source_segments
            FROM voiceprints WHERE person_slug = @s
            """, c, tx);
        cmd.Parameters.AddWithValue("s", slug);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadVoiceprint(r) : null;
    }

    private static Voiceprint ReadVoiceprint(NpgsqlDataReader r)
    {
        var slug = r.GetString(0);
        var centroid = ParseVecLiteral(r.GetString(1));
        var count = r.GetInt32(2);
        var enrolled = DateOnly.FromDateTime(r.GetDateTime(3));
        double? lastMatch = r.IsDBNull(4) ? null : r.GetDouble(4);
        var segs = r.IsDBNull(5)
            ? (IReadOnlyList<string>)Array.Empty<string>()
            : System.Text.Json.JsonSerializer.Deserialize<List<string>>(r.GetString(5)) ?? [];
        return new Voiceprint(slug, centroid, count, enrolled, lastMatch, segs);
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

    private static float[] WeightedCentroid(IReadOnlyList<float> a, int na, IReadOnlyList<float> b, int nb)
    {
        if (a.Count != b.Count) throw new ArgumentException("centroid dimension mismatch on refine");
        var dim = a.Count;
        var total = na + nb;
        var result = new float[dim];
        for (var i = 0; i < dim; i++)
            result[i] = (float)((a[i] * (double)na + b[i] * (double)nb) / total);
        return result;
    }
}
