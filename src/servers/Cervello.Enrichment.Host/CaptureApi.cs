using Cervello.Enrichment.Pipeline;
using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Host;

// ─────────────────────────────────────────────────────────────────────────────────────────────
// The S50 CAPTURE HTTP surface on CT146-cervello — the token-gated transport the CT145 bridge's
// cervello_capture_fact / cervello_set_goal / cervello_link_evidence tools call. Same posture as
// OpenPointsApi / ContextPackApi: bearer-gated (401, fail-closed), content-free, LAN-local, no NATS.
//
//   POST /capture                    {fact, source_hint?, relates_to?, confirm}  → deposit candidate (§5.5)
//   POST /goal                       {name, status?, …, confirm}                 → goal dossier via review-PR (§5.6)
//   POST /goal/{slug}/evidence       {evidence_ref, fact, date?, confirm}        → ## Movimento line via review-PR (§5.7)
//
// CONFIRM-BY-DEFAULT (MC Q6): confirm=false previews (writes nothing); confirm=true deposits / opens
// the review-PR (dry-run by default). Nothing is ever silently merged into map/ — the graph-add human
// gate stands.
// ─────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Maps the token-gated capture + goal HTTP surface (design §5.5/§5.6/§5.7).</summary>
internal static class CaptureApi
{
    private const string BearerPrefix = "Bearer ";

    public static void MapCapture(this WebApplication app)
    {
        // POST /capture — deposit a chat-fact candidate (§5.5). Confirm-by-default.
        app.MapPost("/capture", async (
            HttpRequest req,
            CaptureService svc,
            IOpenPointsAuthGate gate,
            CaptureBody? body,
            CancellationToken ct) =>
        {
            if (AuthFails(req, gate, out var authErr)) return authErr!;
            if (body is null || string.IsNullOrWhiteSpace(body.Fact))
                return BadRequest("fact required");
            try
            {
                var r = await svc.CaptureAsync(
                    body.Fact!, body.Source_hint, (IReadOnlyList<string>?)body.Relates_to ?? Array.Empty<string>(),
                    body.Confirm, DateOnly.FromDateTime(DateTime.UtcNow), ct);
                return Results.Json(new
                {
                    status = r.Status,
                    deposit_id = r.DepositId,
                    path = r.Path,
                    commit = r.Commit,
                    will_enter = r.WillEnter,
                    basis = r.Basis,
                    source = r.Source,
                    preview = r.Preview,
                }, statusCode: 200);
            }
            catch (ArgumentException ex) { return BadRequest(ex.Message); }
        });

        // POST /goal — create/update a goal dossier via a review-PR (§5.6). Confirm-by-default.
        app.MapPost("/goal", async (
            HttpRequest req,
            GoalService svc,
            IOpenPointsAuthGate gate,
            SetGoalBody? body,
            CancellationToken ct) =>
        {
            if (AuthFails(req, gate, out var authErr)) return authErr!;
            if (body is null || string.IsNullOrWhiteSpace(body.Name))
                return BadRequest("name required");
            try
            {
                var r = await svc.SetGoalAsync(new SetGoalRequest(
                    body.Name!, body.Status, body.Horizon, body.Objective,
                    body.People, body.Threads, body.Tags, body.Next_steps, body.Source_hint, body.Confirm),
                    DateOnly.FromDateTime(DateTime.UtcNow), ct);
                return Results.Json(new
                {
                    status = r.Status, goal_slug = r.GoalSlug, pr_branch = r.PrBranch,
                    path = r.Path, basis = r.Basis, preview = r.Preview,
                }, statusCode: 200);
            }
            catch (ArgumentException ex) { return BadRequest(ex.Message); }
        });

        // POST /goal/{slug}/evidence — append a sourced ## Movimento line via a review-PR (§5.7).
        app.MapPost("/goal/{slug}/evidence", async (
            HttpRequest req,
            GoalService svc,
            IOpenPointsAuthGate gate,
            string slug,
            EvidenceBody? body,
            CancellationToken ct) =>
        {
            if (AuthFails(req, gate, out var authErr)) return authErr!;
            if (body is null || string.IsNullOrWhiteSpace(body.Evidence_ref) || string.IsNullOrWhiteSpace(body.Fact))
                return BadRequest("evidence_ref and fact required");
            try
            {
                var r = await svc.LinkEvidenceAsync(new LinkEvidenceRequest(
                    slug, body.Evidence_ref!, body.Fact!, body.Date, body.Confirm),
                    DateOnly.FromDateTime(DateTime.UtcNow), ct);
                var status = r.Status == "unknown_goal" ? 404 : 200;
                return Results.Json(new
                {
                    status = r.Status, goal_slug = r.GoalSlug, line = r.Line,
                    source = r.Source, pr_branch = r.PrBranch, basis = r.Basis,
                }, statusCode: status);
            }
            catch (ArgumentException ex) { return BadRequest(ex.Message); }
        });
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    private static bool AuthFails(HttpRequest req, IOpenPointsAuthGate gate, out IResult? error)
    {
        var token = ExtractBearer(req);
        try { gate.Authorize(token); error = null; return false; }
        catch (OpenPointsUnauthorizedException ex)
        {
            error = Results.Json(new { error = "unauthorized", note = ex.Reason }, statusCode: 401);
            return true;
        }
    }

    private static string? ExtractBearer(HttpRequest req)
    {
        var raw = req.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(raw)) return null;
        return raw.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase) ? raw[BearerPrefix.Length..].Trim() : raw.Trim();
    }

    private static IResult BadRequest(string note) => Results.Json(new { error = "bad_request", note }, statusCode: 400);

    // Request bodies (design §5.5/§5.6/§5.7). Field names carry the wire's snake_case so a
    // System.Text.Json bind (web defaults) matches both camel + snake incoming keys.
    public sealed record CaptureBody(string? Fact, string? Source_hint, List<string>? Relates_to, bool Confirm = false);
    public sealed record SetGoalBody(
        string? Name, string? Status, string? Horizon, string? Objective,
        List<string>? People, List<string>? Threads, List<string>? Tags, List<string>? Next_steps,
        string? Source_hint, bool Confirm = false);
    public sealed record EvidenceBody(string? Evidence_ref, string? Fact, string? Date, bool Confirm = false);
}
