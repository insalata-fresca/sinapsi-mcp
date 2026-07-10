using Cervello.Enrichment.Ports;
using Npgsql;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// Live CT146 Postgres <see cref="IRecordingAudioRefResolver"/> (design <c>ste/cervello</c>
/// <c>docs/design/voiceprint-naming.md</c> §7 phase V4). Reads the SAME <c>watcher_recording</c>
/// table <see cref="Host.Drain.PgNormalizedWorkQueue"/> already reads for the drain lease — a
/// READ-ONLY, ADDITIVE query keyed by <c>recording_id</c> instead of <c>state</c>. We do NOT own or
/// create this table (the Watcher's <c>EnsureSchemaAsync</c> is its authoritative owner) — mirrors
/// <see cref="Host.Drain.PgNormalizedWorkQueue"/>'s own custody note.
///
/// <para>The audio format is not persisted on the row (same gap <see cref="Host.Drain.PgNormalizedWorkQueue"/>
/// documents); the Watcher pairs audio/mp4 (<c>.m4a</c>) recordings, so a fixed <c>"m4a"</c> is
/// supplied here too — <see cref="Pipeline.FfmpegAudioClipCutter"/> re-probes the container either
/// way (it passes <c>-f</c> explicitly rather than trusting the hint blindly).</para>
/// </summary>
public sealed class PgRecordingAudioRefResolver : IRecordingAudioRefResolver
{
    private const string Sql = """
        SELECT audio_sha256
        FROM watcher_recording
        WHERE recording_id = @rec
        """;

    private const string Format = "m4a"; // the Watcher pairs audio/mp4 (.m4a) recordings.

    private readonly string _connString;

    public PgRecordingAudioRefResolver(EnrichmentConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        _connString = cfg.PostgresDsn;
    }

    public async Task<RecordingAudioRef?> ResolveAsync(string recordingId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(recordingId)) return null;

        await using var c = new NpgsqlConnection(_connString);
        await c.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(Sql, c);
        cmd.Parameters.AddWithValue("rec", recordingId);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
            return null; // unknown recording id

        var sha = r.IsDBNull(0) ? "" : r.GetString(0);
        if (string.IsNullOrWhiteSpace(sha))
            return null; // transcript-only recording — no audio side to resolve

        return new RecordingAudioRef(recordingId, sha, Format);
    }
}
