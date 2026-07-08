using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// Live CT146 Postgres idempotency ledger (<see cref="IEnrichmentLedger"/>; SCHEMAS §5): records
/// which <c>rec:&lt;recordingId&gt;:&lt;audio-sha256&gt;</c> keys were picked up so a replay of a
/// seen key is a no-op. The atomic claim is an <c>INSERT … ON CONFLICT DO NOTHING</c> — the
/// affected-row count is the "did I claim it" signal, mirroring
/// <see cref="InMemoryEnrichmentLedger"/>'s <c>TryAdd</c> exactly. Never git.
///
/// <para>L2 verification deferred (like PostgresStateStore): compiles + DI-registered; SQL/DDL
/// asserted by review + the opt-in offline integration test; LIVE behaviour is an L2 step.</para>
/// </summary>
public sealed class PgEnrichmentLedger : IEnrichmentLedger, ISchemaInitializer
{
    public string SchemaName => "enrichment_ledger";

    private const string Ddl = """
        CREATE TABLE IF NOT EXISTS enrichment_ledger (
            idempotency_key TEXT PRIMARY KEY,
            claimed_at      TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """;

    private readonly string _connString;
    private readonly ILogger _log;

    public PgEnrichmentLedger(EnrichmentConfig cfg, ILogger<PgEnrichmentLedger>? log = null)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        _connString = cfg.PostgresDsn;
        _log = log ?? NullLogger<PgEnrichmentLedger>.Instance;
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
                _log.LogWarning(e, "ledger schema-ensure attempt {Attempt}/{Max} failed; retry in {Delay}s",
                    attempt, maxAttempts, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
        }
    }

    public async Task<bool> TryClaimAsync(string idempotencyKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("idempotencyKey must be non-empty", nameof(idempotencyKey));
        await using var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO enrichment_ledger (idempotency_key) VALUES (@k) ON CONFLICT DO NOTHING", c);
        cmd.Parameters.AddWithValue("k", idempotencyKey);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> IsClaimedAsync(string idempotencyKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) return false;
        await using var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT 1 FROM enrichment_ledger WHERE idempotency_key = @k", c);
        cmd.Parameters.AddWithValue("k", idempotencyKey);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }
}
