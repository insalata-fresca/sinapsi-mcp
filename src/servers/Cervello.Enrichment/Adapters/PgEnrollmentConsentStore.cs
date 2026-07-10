using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// Live CT146 Postgres <see cref="IEnrollmentConsentStore"/> (design <c>ste/cervello</c>
/// <c>docs/design/voiceprint-naming.md</c> §6.4 + §9 fork 1). The durable, runtime-mutable extension
/// of the §10 allowlist the V5 rename-poller writes: a Drive rename adds the person's slug here, and
/// the voiceprint store's §10 gate UNIONs these slugs with the static
/// <see cref="Cervello.Enrichment.Domain.EnrollmentAllowlist"/>.
///
/// <para>Non-biometric: person slug + the authorising basis id + a timestamp only — never a centroid
/// (that stays CT146 pgvector-only). Mirrors the other Pg stores' <see cref="ISchemaInitializer"/>
/// pattern (retried DDL on startup).</para>
/// </summary>
public sealed class PgEnrollmentConsentStore : IEnrollmentConsentStore, ISchemaInitializer
{
    public string SchemaName => "voiceprint_enrollment_consent";

    private const string Ddl = """
        CREATE TABLE IF NOT EXISTS voiceprint_enrollment_consent (
            person_slug  TEXT PRIMARY KEY,
            basis_id     TEXT NOT NULL,
            consented_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """;

    private readonly string _connString;
    private readonly ILogger _log;

    public PgEnrollmentConsentStore(EnrichmentConfig cfg, ILogger<PgEnrollmentConsentStore>? log = null)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        _connString = cfg.PostgresDsn;
        _log = log ?? NullLogger<PgEnrollmentConsentStore>.Instance;
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
                _log.LogWarning(e, "enrollment-consent schema-ensure attempt {Attempt}/{Max} failed; retrying in {Delay}s",
                    attempt, maxAttempts, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
        }
    }

    public async Task<bool> AddConsentAsync(string personSlug, string basisId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(personSlug))
            throw new ArgumentException("personSlug must be non-empty", nameof(personSlug));

        await using var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO voiceprint_enrollment_consent (person_slug, basis_id)
            VALUES (@s, @b) ON CONFLICT (person_slug) DO NOTHING
            """, c);
        cmd.Parameters.AddWithValue("s", personSlug);
        cmd.Parameters.AddWithValue("b", basisId ?? "");
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows > 0;
    }

    public async Task<bool> IsConsentedAsync(string personSlug, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(personSlug)) return false;
        await using var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT 1 FROM voiceprint_enrollment_consent WHERE person_slug = @s", c);
        cmd.Parameters.AddWithValue("s", personSlug);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }
}
