using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// Live CT146 Postgres <see cref="IDeltaCursorStore"/> — the per-caller delta baseline cursor
/// (design §2.6, MC Q9: server-side, per-caller). One row per (caller identity hash, intent) holding
/// the last-sweep <c>as_of</c>; <see cref="AdvanceAsync"/> upserts it. Reuses the same
/// <see cref="ISchemaInitializer"/> migration posture as the other Pg stores so the host ensures the
/// table on startup. Content-free: only an opaque caller key + an intent + a timestamp — never
/// personal data.
///
/// <para>L2 verification deferred (like <see cref="PgOpenPointStore"/>): compiles + DI-registered;
/// DDL/SQL asserted by review; LIVE behaviour is an L2 step.</para>
/// </summary>
public sealed class PgDeltaCursorStore : IDeltaCursorStore, ISchemaInitializer
{
    public string SchemaName => "pack_delta_cursor";

    private const string Ddl = """
        CREATE TABLE IF NOT EXISTS pack_delta_cursor (
            caller_key  TEXT NOT NULL,
            intent      TEXT NOT NULL,
            as_of       TEXT NOT NULL,
            updated_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
            PRIMARY KEY (caller_key, intent)
        );
        """;

    private readonly string _connString;
    private readonly ILogger _log;

    public PgDeltaCursorStore(EnrichmentConfig cfg, ILogger<PgDeltaCursorStore>? log = null)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        _connString = cfg.PostgresDsn;
        _log = log ?? NullLogger<PgDeltaCursorStore>.Instance;
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
                _log.LogWarning(e, "pack_delta_cursor schema attempt {Attempt} failed; retrying in {Delay}s", attempt, delay.TotalSeconds);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    public async Task<string?> GetBaselineAsync(string callerKey, string intent, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT as_of FROM pack_delta_cursor WHERE caller_key = @k AND intent = @i", c);
        cmd.Parameters.AddWithValue("k", callerKey);
        cmd.Parameters.AddWithValue("i", intent);
        var v = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return v as string;
    }

    public async Task AdvanceAsync(string callerKey, string intent, string asOf, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO pack_delta_cursor (caller_key, intent, as_of, updated_at)
            VALUES (@k, @i, @a, now())
            ON CONFLICT (caller_key, intent) DO UPDATE SET as_of = EXCLUDED.as_of, updated_at = now();
            """, c);
        cmd.Parameters.AddWithValue("k", callerKey);
        cmd.Parameters.AddWithValue("i", intent);
        cmd.Parameters.AddWithValue("a", asOf);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
