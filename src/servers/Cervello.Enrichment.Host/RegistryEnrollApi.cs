using Cervello.Enrichment;
using Cervello.Enrichment.Pipeline;

namespace Cervello.Enrichment.Host;

// ────────────────────────────────────────────────────────────────────────────────────────────────────
// The REGISTRY-PILOT enrol trigger (design ste/cervello docs/design/voiceprint-naming.md §7 phase V5, §10):
// an on-demand HTTP endpoint that seeds the enrolled voiceprint store from the operator's human-named clips
// already sitting in the Drive voiceprints/registry/ folder — the pilot bootstrap the V5 rename-poller
// cannot do (those clips have no VoiceprintNamingCandidate row). Runs RegistryClipEnroller once and reports
// what it enrolled. Mirrors VoiceSamplesApi / OpenPointsApi shape EXACTLY: a thin Map* extension,
// bearer-gated with the SAME operator token (CERVELLO_OPEN_POINTS_TOKEN) — this is an OPERATOR-triggered
// admin action (embed clips via the live sidecar, write centroids), never an unauthenticated surface.
//
//   POST /v1/voiceprints/enroll-registry   { "slugs": ["stefano-ursino", ...] }
//     → { enrolled:[{slug,clip_name,drive_file_id,was_refine,sample_count}], skipped:[{slug,clip_name,reason}] }
//
// Isolation: the response carries slugs, clip names, Drive file ids, and skip reasons ONLY — NEVER a
// centroid/embedding (that stays CT146-side inside the store, confinement). The §10 gate is enforced in the
// store: the enroller records each slug's consent (the operator naming the clip IS the consent) before the
// enrol, so nobody enrols who the operator did not name.
// ────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Maps the registry-pilot enrol trigger endpoint.</summary>
internal static class RegistryEnrollApi
{
    private const string BearerPrefix = "Bearer ";

    /// <summary>The request body: the exact person slugs to enrol from their registry clip(s).</summary>
    public sealed record EnrollRegistryRequest(string[]? Slugs);

    public static void MapRegistryEnroll(this WebApplication app)
    {
        app.MapPost("/v1/voiceprints/enroll-registry", async (
            HttpRequest req,
            EnrollRegistryRequest? body,
            RegistryClipEnroller enroller,
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

            var slugs = body?.Slugs?.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray() ?? [];
            if (slugs.Length == 0)
                return Results.Json(new { error = "bad_request", note = "slugs must be a non-empty array" }, statusCode: 400);

            try
            {
                var on = DateOnly.FromDateTime(DateTime.UtcNow);
                var result = await enroller.EnrollAsync(slugs, on, ct);
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

    // NEVER serialises a centroid/embedding — biometric material stays CT146-side (confinement).
    private static object ToWire(RegistryEnrollResult r) => new
    {
        enrolled_count = r.Enrolled.Count,
        skipped_count = r.Skipped.Count,
        enrolled = r.Enrolled.Select(e => new
        {
            slug = e.Slug,
            clip_name = e.ClipName,
            drive_file_id = e.DriveFileId,
            was_refine = e.WasRefine,
            sample_count = e.SampleCount,
        }).ToList(),
        skipped = r.Skipped.Select(s => new { slug = s.Slug, clip_name = s.ClipName, reason = s.Reason }).ToList(),
    };
}
