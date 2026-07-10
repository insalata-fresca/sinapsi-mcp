using Cervello.Enrichment;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Pipeline;

namespace Cervello.Enrichment.Host;

// ─────────────────────────────────────────────────────────────────────────────────────────────
// The V4 sample-generation trigger (design ste/cervello docs/design/voiceprint-naming.md §7 phase
// V4): an on-demand HTTP endpoint that runs VoiceSampleGenerator once and reports what it produced.
// Mirrors OpenPointsApi's shape (a thin Map* extension, minimal API, bearer-gated with the SAME
// operator token) — this is an OPERATOR-triggered admin action (cut + upload ~15 clips, mint a
// gdrive bearer, write a Pg table), never an unauthenticated surface.
//
//   POST /v1/voiceprints/generate-samples[?max=15]  → {uploaded:[...], skipped:[...], deleted:[...]}
//
// Isolation: the response carries sample names, Drive file ids, and skip reasons only — NEVER a
// centroid/embedding (that stays CT146-side inside VoiceprintNamingCandidate, never serialised here).
// ─────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Maps the V4 sample-generation trigger endpoint.</summary>
internal static class VoiceSamplesApi
{
    private const string BearerPrefix = "Bearer ";

    public static void MapVoiceSamples(this WebApplication app, EnrichmentConfig cfg)
    {
        app.MapPost("/v1/voiceprints/generate-samples", async (
            HttpRequest req,
            VoiceSampleGenerator generator,
            int? max,
            CancellationToken ct) =>
        {
            var token = ExtractBearer(req);
            var expected = Environment.GetEnvironmentVariable("CERVELLO_OPEN_POINTS_TOKEN");
            if (string.IsNullOrEmpty(expected))
                return Results.Json(new { error = "unauthorized", note = "gate_not_configured" }, statusCode: 401);
            if (string.IsNullOrEmpty(token) ||
                !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(token), System.Text.Encoding.UTF8.GetBytes(expected)))
                return Results.Json(new { error = "unauthorized", note = "missing_or_invalid_bearer" }, statusCode: 401);

            var maxCandidates = max is > 0 ? max.Value : cfg.VoiceSamplesMax;

            try
            {
                var result = await generator.GenerateAsync(maxCandidates, ct);
                return Results.Json(ToWire(result), statusCode: 200);
            }
            catch (ArgumentException ex)
            {
                return Results.Json(new { error = "bad_request", note = ex.Message }, statusCode: 400);
            }
        });
    }

    private static string? ExtractBearer(HttpRequest req)
    {
        var raw = req.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(raw)) return null;
        return raw.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? raw[BearerPrefix.Length..].Trim()
            : raw.Trim();
    }

    private static object ToWire(GenerateResult r) => new
    {
        uploaded_count = r.Uploaded.Count,
        skipped_count = r.Skipped.Count,
        uploaded = r.Uploaded.Select(ToWire).ToList(),
        skipped = r.Skipped.Select(s => new { sample_name = s.SampleName, voice = s.VoiceLocalId, reason = s.Reason }).ToList(),
        deleted_stale_drive_file_ids = r.DeletedStaleDriveFileIds,
    };

    // NEVER serialises the centroid — biometric material stays CT146-side (confinement).
    private static object ToWire(VoiceprintNamingCandidate c) => new
    {
        sample_name = c.SampleName,
        drive_file_id = c.DriveFileId,
        source_recordings = c.SourceMembers.Select(m => m.RecordingId).Distinct(StringComparer.Ordinal).ToList(),
        created_at = c.CreatedAt,
    };
}
