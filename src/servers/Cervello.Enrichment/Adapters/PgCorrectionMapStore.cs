using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// Live CT146 Postgres <see cref="ICorrectionMapStore"/> (design Data Model → <c>correction_map</c>;
/// DESIGN §5.2 step 3, §6.1 learning signal). The historized glossary the correction pass reads to
/// build its grounding context; an operator answer to a correction open-point UPSERTs an entry so
/// the same term auto-corrects next time (spec <c>text-correction</c> → "Operator answer feeds the
/// correction map"). Idempotent on <c>(before, kind)</c> — a later answer updates it. Never git.
///
/// <para>L2 verification deferred (like PostgresStateStore): compiles + DI-registered; SQL/DDL
/// asserted by review + the opt-in offline pgvector integration test. LIVE behaviour is an L2 step.</para>
/// </summary>
public sealed class PgCorrectionMapStore : ICorrectionMapStore
{
    private const string Ddl = """
        CREATE TABLE IF NOT EXISTS correction_map (
            term_before        TEXT NOT NULL,
            kind               TEXT NOT NULL,
            term_after         TEXT NOT NULL,
            confirmed_answer_id TEXT,
            created_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
            PRIMARY KEY (term_before, kind)
        );
        """;

    private readonly string _connString;
    private readonly ILogger _log;

    public PgCorrectionMapStore(EnrichmentConfig cfg, ILogger<PgCorrectionMapStore>? log = null)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        _connString = cfg.PostgresDsn;
        _log = log ?? NullLogger<PgCorrectionMapStore>.Instance;
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
                _log.LogWarning(e, "correction-map schema-ensure attempt {Attempt}/{Max} failed; retry in {Delay}s",
                    attempt, maxAttempts, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
        }
    }

    public async Task<IReadOnlyList<GlossaryEntry>> GetGlossaryAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT term_before, term_after, kind, confirmed_answer_id FROM correction_map", c);
        var outp = new List<GlossaryEntry>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var kind = Enum.TryParse<CorrectionKind>(r.GetString(2), ignoreCase: true, out var k) ? k : CorrectionKind.Term;
            outp.Add(new GlossaryEntry(r.GetString(0), r.GetString(1), kind, r.IsDBNull(3) ? null : r.GetString(3)));
        }
        return outp;
    }

    public async Task UpsertAsync(GlossaryEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO correction_map (term_before, kind, term_after, confirmed_answer_id, created_at)
            VALUES (@before, @kind, @after, @ans, now())
            ON CONFLICT (term_before, kind) DO UPDATE SET
                term_after = EXCLUDED.term_after, confirmed_answer_id = EXCLUDED.confirmed_answer_id
            """, c);
        cmd.Parameters.AddWithValue("before", entry.Before);
        cmd.Parameters.AddWithValue("kind", entry.Kind.ToString());
        cmd.Parameters.AddWithValue("after", entry.After);
        cmd.Parameters.AddWithValue("ans", (object?)entry.ConfirmedAnswerId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
