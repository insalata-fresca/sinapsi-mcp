using Cervello.Enrichment.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Cervello.Enrichment.Host.Drain;

/// <summary>
/// Live CT146 Postgres drain source (<see cref="INormalizedWorkQueue"/>). It reads the
/// <c>watcher_recording</c> table the M6 <c>Cervello.Watcher</c> already writes, filtered to
/// <c>state = 'normalized'</c> (SCHEMAS §5 wire name) — a READ-ONLY, ADDITIVE view that needs NO
/// watcher-side change (the Watcher already persists the <c>normalized</c> signal this drains).
/// <see cref="AdvanceStateAsync"/> is the ONE write this host makes to the shared row: an
/// <c>UPDATE … WHERE recording_key = @k</c> advancing the row's <c>state</c> to the §5 wire name of
/// the engine's post-drain state. The two components therefore SHARE the row (E4 enum reconciliation).
///
/// <para><b>Format / language.</b> The Watcher's row does not persist audio format or language, so
/// the drain adapter supplies them from config: <see cref="_language"/> (the engine's
/// <c>CERVELLO_TRANSCRIBE_LANGUAGE</c>) and a neutral <c>_format</c> default. The audio bytes stay in
/// the CT staging blob store and are fetched transiently by a stage — never carried here (custody).</para>
///
/// <para><b>L2 verification deferred</b> (like <c>PgEnrichmentLedger</c> / the Watcher's
/// <c>PostgresStateStore</c>): this compiles + is DI-registered, its SQL is asserted by review + the
/// opt-in offline integration test, and its LIVE behaviour is an L2 (on-CT) step — out of E-HOST
/// scope. The drain-loop discipline is proven against <see cref="InMemoryNormalizedWorkQueue"/>.</para>
/// </summary>
public sealed class PgNormalizedWorkQueue : INormalizedWorkQueue
{
    // The row shape is owned by Cervello.Watcher.State.PostgresStateStore; we only READ it (+ the one
    // state UPDATE). We do NOT CREATE it — the Watcher's EnsureSchemaAsync is its authoritative owner.
    private const string LeaseSql = """
        SELECT recording_id, audio_sha256
        FROM watcher_recording
        WHERE state = 'normalized'
        ORDER BY ready_at ASC
        LIMIT @max
        """;

    private const string AdvanceSql = """
        UPDATE watcher_recording
        SET state = @state
        WHERE recording_key = @key
        """;

    private readonly string _connString;
    private readonly string _language;
    private readonly string _format;
    private readonly ILogger _log;

    public PgNormalizedWorkQueue(EnrichmentConfig cfg, ILogger<PgNormalizedWorkQueue>? log = null)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        _connString = cfg.PostgresDsn;
        _language = cfg.TranscribeLanguage;
        _format = "m4a"; // the Watcher pairs audio/mp4 (.m4a) recordings; the stage re-probes the blob.
        _log = log ?? NullLogger<PgNormalizedWorkQueue>.Instance;
    }

    private async ValueTask<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var c = new NpgsqlConnection(_connString);
        await c.OpenAsync(ct).ConfigureAwait(false);
        return c;
    }

    public async Task<IReadOnlyList<RecordingRef>> LeaseNormalizedAsync(int max, CancellationToken ct = default)
    {
        if (max <= 0) throw new ArgumentOutOfRangeException(nameof(max), "max must be positive");

        var batch = new List<RecordingRef>();
        await using var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(LeaseSql, c);
        cmd.Parameters.AddWithValue("max", max);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var id = r.GetString(0);
            var sha = r.GetString(1);
            // ready == true: the row is at `normalized`, which is precisely the Watcher's
            // "ready for enrichment" terminal (the local ready-marker equivalent, D-side).
            batch.Add(new RecordingRef(id, sha, _format, _language, ready: true));
        }
        return batch;
    }

    public async Task AdvanceStateAsync(RecordingRef recording, EnrichmentState state, string? reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recording);
        // recording_key == the §8 idempotency key `rec:<id>:<audio_sha256>` (Recording.RecordingKey).
        var key = recording.IdempotencyKey;
        var wire = EnrichmentStateMachine.Name(state);
        await using var c = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(AdvanceSql, c);
        cmd.Parameters.AddWithValue("state", wire);
        cmd.Parameters.AddWithValue("key", key);
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        if (rows == 0)
            _log.LogWarning("advance {Key} → {State}: no matching recordings row (already advanced or absent)", key, wire);
        else
            _log.LogInformation("advance {Key} → {State}{Reason}", key, wire, reason is null ? "" : $" ({reason})");
    }
}
