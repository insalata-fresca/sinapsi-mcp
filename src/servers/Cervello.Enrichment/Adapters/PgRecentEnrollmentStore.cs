using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// Live CT146 Postgres <see cref="IRecentEnrollmentStore"/> (design <c>ste/cervello</c>
/// <c>docs/design/voiceprint-naming.md</c> §7 phase V6, §6.6). The scoped, bounded "just human-enrolled"
/// signal V5 writes + V6's re-attribution consumes, so an auto-band corpus match to a just-enrolled
/// print auto-applies (carrying the enrollment's <c>human://</c> basis) while the global phase is still
/// escalate-only. Non-biometric: slug + basis id + a timestamp only.
/// </summary>
public sealed class PgRecentEnrollmentStore : IRecentEnrollmentStore, ISchemaInitializer
{
    public string SchemaName => "voiceprint_recent_enrollment";

    private const string Ddl = """
        CREATE TABLE IF NOT EXISTS voiceprint_recent_enrollment (
            person_slug   TEXT PRIMARY KEY,
            human_basis   TEXT NOT NULL,
            enrolled_at   TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """;

    private readonly string _connString;
    private readonly int _ttlMinutes;
    private readonly ILogger _log;

    public PgRecentEnrollmentStore(EnrichmentConfig cfg, ILogger<PgRecentEnrollmentStore>? log = null)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        _connString = cfg.PostgresDsn;
        _ttlMinutes = cfg.RecentEnrollmentTtlMinutes;
        _log = log ?? NullLogger<PgRecentEnrollmentStore>.Instance;
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
                _log.LogWarning(e, "recent-enrollment schema-ensure attempt {Attempt}/{Max} failed; retrying in {Delay}s",
                    attempt, maxAttempts, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
        }
    }

    public async Task MarkAsync(string personSlug, string humanBasisId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(personSlug))
            throw new ArgumentException("personSlug must be non-empty", nameof(personSlug));
        if (string.IsNullOrWhiteSpace(humanBasisId))
            throw new ArgumentException("humanBasisId must be non-empty", nameof(humanBasisId));

        await using var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO voiceprint_recent_enrollment (person_slug, human_basis)
            VALUES (@s, @b)
            ON CONFLICT (person_slug) DO UPDATE SET human_basis = EXCLUDED.human_basis, enrolled_at = now()
            """, c);
        cmd.Parameters.AddWithValue("s", personSlug);
        cmd.Parameters.AddWithValue("b", humanBasisId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetBasisAsync(string personSlug, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(personSlug)) return null;
        await using var c = await OpenAsync(ct);
        // Write-safety bound (§9 fork 2 / MC pass): return the basis ONLY if the mark is STILL WITHIN the
        // TTL window (enrolled_at >= now() - ttl). A stale mark returns null — it must NOT authorise an
        // auto-apply, so a later unrelated ≥-auto match to this slug escalates under escalate-only.
        await using var cmd = new NpgsqlCommand("""
            SELECT human_basis FROM voiceprint_recent_enrollment
            WHERE person_slug = @s
              AND enrolled_at >= now() - make_interval(mins => @ttl)
            """, c);
        cmd.Parameters.AddWithValue("s", personSlug);
        cmd.Parameters.AddWithValue("ttl", _ttlMinutes);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }
}
