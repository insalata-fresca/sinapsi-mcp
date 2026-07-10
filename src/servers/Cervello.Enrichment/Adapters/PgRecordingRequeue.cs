using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// Live CT146 Postgres <see cref="IRecordingRequeue"/> (design <c>ste/cervello</c>
/// <c>docs/design/voiceprint-naming.md</c> §7 phase V6, §1.6/§6.6). Resets a matching recording's
/// shared <c>watcher_recording.state</c> to <c>normalized</c> (the SCHEMAS §5 wire name the drain
/// leases on) so the existing <c>DrainWorker</c> re-runs the pipeline against the now-enrolled print.
///
/// <para>This is the SAME shared row <see cref="Cervello.Enrichment.Host.Drain.PgNormalizedWorkQueue"/>
/// reads/advances (E4 enum reconciliation) — a single, targeted <c>UPDATE … WHERE recording_id = @id</c>.
/// The table is owned + created by the Watcher; this adapter only WRITES the one state column (like the
/// drain's own <c>AdvanceStateAsync</c>), it never CREATEs the table.</para>
/// </summary>
public sealed class PgRecordingRequeue : IRecordingRequeue
{
    private readonly string _connString;
    private readonly ILogger _log;

    public PgRecordingRequeue(EnrichmentConfig cfg, ILogger<PgRecordingRequeue>? log = null)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        _connString = cfg.PostgresDsn;
        _log = log ?? NullLogger<PgRecordingRequeue>.Instance;
    }

    public async Task<bool> RequeueForReattributionAsync(string recordingId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(recordingId))
            throw new ArgumentException("recordingId must be non-empty", nameof(recordingId));

        await using var c = new NpgsqlConnection(_connString);
        await c.OpenAsync(ct).ConfigureAwait(false);
        // Reset to the §5 wire name `normalized` (lowercase — the form the drain lease selects on). We
        // scope to a single recording id (targeted, never blanket) and reset regardless of the current
        // post-enrichment state so a fully-enriched recording is re-attributed against the new print.
        await using var cmd = new NpgsqlCommand(
            "UPDATE watcher_recording SET state = 'normalized' WHERE recording_id = @id", c);
        cmd.Parameters.AddWithValue("id", recordingId);
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        if (rows == 0)
            _log.LogWarning("requeue-for-reattribution {Rec}: no matching watcher_recording row", recordingId);
        else
            _log.LogInformation("requeue-for-reattribution {Rec}: reset {Rows} row(s) → normalized", recordingId, rows);
        return rows > 0;
    }
}
