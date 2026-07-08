using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Pipeline;
using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Host;

// ─────────────────────────────────────────────────────────────────────────────────────────────
// The S50 L3 open-points HTTP surface on CT146-cervello — the token-gated transport that fronts
// the already-built OpenPointsService (E5/L1) so the operator's claude.ai app (Surface A, via the
// CT145 bridge) can LIST pending open-points and ANSWER them. It mirrors M5's cervello_search
// posture: bearer-gated, scoped, access-logged, redacted; NEVER an unauthenticated read of the
// private plane (SearchAuth lesson). Two routes only, co-hosted on the same bind as /healthz:
//
//   GET  /open-points[?kind=speaker|correction|link][&recording=<id>]  → redacted pending list
//   POST /open-points/{pointId}/answer  {mode:select|value|dismiss, value?}  → apply + write-back
//
// Auth: the Bearer is validated by IOpenPointsAuthGate BEFORE any store I/O. The engine service
// re-authorizes internally too (defence in depth), but we authorize at the edge to map a
// missing/invalid bearer to a clean 401 (never 500, never a silent success). The gate FAILS
// CLOSED on an empty configured token, so a mis-provisioned deploy exposes nothing.
//
// Isolation: responses carry only redacted OpenPointViews (refs + question + scored candidates —
// lint R10); no transcript body, audio, or embedding ever crosses this surface. The host opens no
// NATS (invariant 3 / D8) — this HTTP surface is a LAN-local read/write against the on-CT store,
// reachable only from the bridge (CT145) via the CT146 ingress allow. Every call is appended to
// the on-CT access log inside OpenPointsService.
// ─────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Maps the token-gated open-points HTTP surface (S50 L3 exposure).</summary>
internal static class OpenPointsApi
{
    private const string BearerPrefix = "Bearer ";

    public static void MapOpenPoints(this WebApplication app)
    {
        // GET /open-points — the operator's pending queue, redacted + optionally filtered.
        app.MapGet("/open-points", async (
            HttpRequest req,
            OpenPointsService svc,
            string? kind,
            string? recording,
            CancellationToken ct) =>
        {
            var token = ExtractBearer(req);
            OpenPointKind? kindFilter;
            try
            {
                kindFilter = ParseKind(kind);
            }
            catch (ArgumentException ex)
            {
                return Results.Json(new { error = "bad_request", note = ex.Message }, statusCode: 400);
            }

            try
            {
                var views = await svc.ListAsync(token, kindFilter, recording, ct);
                return Results.Json(new
                {
                    count = views.Count,
                    open_points = views.Select(ToWire).ToList(),
                }, statusCode: 200);
            }
            catch (OpenPointsUnauthorizedException ex)
            {
                return Unauthorized(ex);
            }
        });

        // POST /open-points/{pointId}/answer — apply the confirmed fact (write-back) or dismiss.
        app.MapPost("/open-points/{pointId}/answer", async (
            HttpRequest req,
            OpenPointsService svc,
            string pointId,
            AnswerBody? body,
            CancellationToken ct) =>
        {
            var token = ExtractBearer(req);
            if (body is null)
                return Results.Json(new { error = "bad_request", note = "answer body required" }, statusCode: 400);

            OpenPointAnswer answer;
            try
            {
                answer = ToAnswer(body);
            }
            catch (ArgumentException ex)
            {
                return Results.Json(new { error = "bad_request", note = ex.Message }, statusCode: 400);
            }

            try
            {
                var result = await svc.AnswerAsync(token, pointId, answer, DateOnly.FromDateTime(DateTime.UtcNow), ct);
                var status = result.Status == AnswerStatus.UnknownPoint ? 404 : 200;
                return Results.Json(ToWire(result), statusCode: status);
            }
            catch (OpenPointsUnauthorizedException ex)
            {
                return Unauthorized(ex);
            }
            catch (ArgumentException ex)
            {
                // e.g. a Select whose value isn't a candidate of the point.
                return Results.Json(new { error = "bad_request", note = ex.Message }, statusCode: 400);
            }
        });
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    private static string? ExtractBearer(HttpRequest req)
    {
        var raw = req.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(raw)) return null;
        return raw.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? raw[BearerPrefix.Length..].Trim()
            : raw.Trim();
    }

    private static IResult Unauthorized(OpenPointsUnauthorizedException ex) =>
        Results.Json(new { error = "unauthorized", note = ex.Reason }, statusCode: 401);

    private static OpenPointKind? ParseKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "speaker" => OpenPointKind.Speaker,
        "correction" => OpenPointKind.Correction,
        // "link" is the wire token OpenPointView emits for OpenPointKind.Fact.
        "link" or "fact" or "timeline" => OpenPointKind.Fact,
        _ => throw new ArgumentException($"unknown kind '{kind}' (expected speaker|correction|link)"),
    };

    private static OpenPointAnswer ToAnswer(AnswerBody body)
    {
        var mode = (body.Mode ?? "").Trim().ToLowerInvariant();
        return mode switch
        {
            "select" => OpenPointAnswer.Select(body.Value ?? throw new ArgumentException("select requires 'value'")),
            "value" => OpenPointAnswer.Value_(body.Value ?? throw new ArgumentException("value requires 'value'")),
            "dismiss" => OpenPointAnswer.Dismiss(),
            _ => throw new ArgumentException($"unknown mode '{body.Mode}' (expected select|value|dismiss)"),
        };
    }

    private static object ToWire(OpenPointView v) => new
    {
        point_id = v.PointId,
        kind = v.KindWire,
        recording = v.Recording,
        bundle = v.Bundle,
        question = v.Question,
        candidates = v.Candidates.Select(c => new { value = c.Value, confidence = c.Confidence, why = c.Why }).ToList(),
    };

    private static object ToWire(AnswerResult r) => new
    {
        point_id = r.PointId,
        status = r.Status switch
        {
            AnswerStatus.Applied => "applied",
            AnswerStatus.Dismissed => "dismissed",
            AnswerStatus.AlreadyResolved => "already_resolved",
            AnswerStatus.UnknownPoint => "unknown_point",
            _ => "unknown",
        },
        kind = r.Kind?.ToString().ToLowerInvariant(),
        basis = r.BasisId,
        pr_branch = r.Pr?.Branch,
        enrolled = r.Enrolled,
        glossary_updated = r.GlossaryUpdated,
    };

    /// <summary>The POST answer body: {mode: select|value|dismiss, value?}.</summary>
    public sealed record AnswerBody(string? Mode, string? Value);
}
