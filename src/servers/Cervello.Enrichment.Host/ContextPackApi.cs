using System.Security.Cryptography;
using System.Text;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Pipeline;
using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Host;

// ─────────────────────────────────────────────────────────────────────────────────────────────
// The S50 context-pack + map-read HTTP surface on CT146-cervello — the token-gated transport the
// CT145 bridge's cervello_context_pack / cervello_get / cervello_timeline_walk tools call. Mirrors
// OpenPointsApi's posture EXACTLY: bearer-gated (401 on missing/invalid, fail-closed on an empty
// configured token), content-free logging, LAN-local, no NATS (invariant 3). Three routes:
//
//   POST /context-pack  {intent, focus?, budget?, since?}  → the bounded/ranked/SOURCED pack (§5.1)
//   GET  /object?kind=&id=                                 → one map object verbatim (§5.3)
//   GET  /timeline?anchor=&from=&to=                       → dated sourced movement lines (§5.4)
//
// The heavy lifting (select/rank/bound/summarise/coverage/delta) is the ContextPackAssembler — this
// file is only the transport + the exact wire rendering of the design §2.1 pack JSON.
// ─────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Maps the token-gated context-pack + map-read HTTP surface (design §5.1/§5.3/§5.4).</summary>
internal static class ContextPackApi
{
    private const string BearerPrefix = "Bearer ";

    public static void MapContextPack(this WebApplication app, int defaultBudget)
    {
        // POST /context-pack — the primary retrieval tool (§5.1). The core.
        app.MapPost("/context-pack", async (
            HttpRequest req,
            ContextPackAssembler assembler,
            IOpenPointsAuthGate gate,
            PackRequestBody? body,
            CancellationToken ct) =>
        {
            if (AuthFails(req, gate, out var token, out var authErr)) return authErr!;
            if (body is null || string.IsNullOrWhiteSpace(body.Intent))
                return BadRequest("intent required (goal_reasoning|portfolio|person_prep|recall|thread)");

            PackIntent intent;
            try { intent = ParseIntent(body.Intent); }
            catch (ArgumentException ex) { return BadRequest(ex.Message); }

            var budget = body.Budget is > 0 ? System.Math.Min(body.Budget.Value, 200_000) : defaultBudget;
            var callerKey = CallerKey(token);

            try
            {
                var pack = await assembler.AssembleAsync(
                    new PackRequest(intent, Trim(body.Focus), budget, Trim(body.Since), callerKey), ct);
                return Results.Json(ToWire(pack), statusCode: 200);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
        });

        // GET /object?kind=&id= — one map object verbatim (§5.3).
        app.MapGet("/object", async (
            HttpRequest req,
            IMapGraph graph,
            IOpenPointsAuthGate gate,
            string? kind,
            string? id,
            CancellationToken ct) =>
        {
            if (AuthFails(req, gate, out _, out var authErr)) return authErr!;
            if (string.IsNullOrWhiteSpace(id)) return BadRequest("id (slug) required");
            MapObjectKind k;
            try { k = ParseKind(kind); }
            catch (ArgumentException ex) { return BadRequest(ex.Message); }

            var obj = await graph.GetObjectAsync(k, id!.Trim(), ct);
            if (obj is null)
                return Results.Json(new { error = "not_found", note = $"no {kind} '{id}'" }, statusCode: 404);

            return Results.Json(new
            {
                kind = obj.Kind.ToString().ToLowerInvariant(),
                id = obj.Id,
                frontmatter = obj.Frontmatter,
                body_markdown = obj.BodyMarkdown,
                sources = obj.Sources,
            }, statusCode: 200);
        });

        // GET /timeline?anchor=&from=&to= — dated sourced movement lines (§5.4).
        app.MapGet("/timeline", async (
            HttpRequest req,
            IMapGraph graph,
            IOpenPointsAuthGate gate,
            string? anchor,
            string? from,
            string? to,
            CancellationToken ct) =>
        {
            if (AuthFails(req, gate, out _, out var authErr)) return authErr!;
            if (string.IsNullOrWhiteSpace(anchor))
                return BadRequest("anchor required (goal:<slug>|person:<slug>|thread:<slug>|global)");

            var lines = await graph.WalkTimelineAsync(anchor!.Trim(), Trim(from), Trim(to), ct);
            return Results.Json(new
            {
                anchor,
                from,
                to,
                lines = lines.Select(l => new { date = l.Date, fact = l.Fact, links = l.Links, source = l.Source }).ToList(),
            }, statusCode: 200);
        });
    }

    // ── wire rendering: the exact design §2.1 pack JSON ──────────────────────────────────────────

    internal static object ToWire(ContextPack p) => new PackWire
    {
        intent = IntentWire(p.Intent),
        focus = p.Focus,
        budget = p.Budget,
        used = p.Used,
        as_of = p.AsOf,
        sections = p.Sections.Select(s => new SectionWire
        {
            section = s.Section,
            items = s.Items.Select(i => new ItemWire { content = i.Content, source = i.Source, confidence = i.Confidence }).ToList(),
        }).ToList(),
        coverage = new CoverageWire { looked_at = p.Coverage.LookedAt, deferred = p.Coverage.Deferred, gaps = p.Coverage.Gaps },
        open_points = p.OpenPoints.Select(o => new OpenPointWire { point_id = o.PointId, question = o.Question, kind = o.Kind }).ToList(),
        // Only present when populated (goal_reasoning + portfolio).
        delta = p.Delta is null ? null : new DeltaWire
        {
            since = p.Delta.Since,
            new_evidence = p.Delta.NewEvidence.Select(e => new EvidenceWire { date = e.Date, fact = e.Fact, source = e.Source }).ToList(),
            status_changes = p.Delta.StatusChanges.Select(s => new StatusChangeWire { focus = s.Focus, from = s.From, to = s.To }).ToList(),
        },
        // Only present when a recall focus was ambiguous.
        disambiguation = p.Disambiguation?.Select(d => new DisambiguationWire { candidate = d.Candidate, descriptor = d.Descriptor, evidence_count = d.EvidenceCount }).ToList(),
    };

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    private static bool AuthFails(HttpRequest req, IOpenPointsAuthGate gate, out string? token, out IResult? error)
    {
        token = ExtractBearer(req);
        try
        {
            gate.Authorize(token);
            error = null;
            return false;
        }
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

    /// <summary>The delta cursor key (design §2.6): a hash of the bearer (jwt:{sub} is not available at this edge).</summary>
    private static string CallerKey(string? token)
    {
        if (string.IsNullOrEmpty(token)) return "anon";
        return "bearer:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant()[..16];
    }

    private static IResult BadRequest(string note) => Results.Json(new { error = "bad_request", note }, statusCode: 400);

    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static PackIntent ParseIntent(string intent) => intent.Trim().ToLowerInvariant() switch
    {
        "goal_reasoning" => PackIntent.GoalReasoning,
        "portfolio" => PackIntent.Portfolio,
        "person_prep" => PackIntent.PersonPrep,
        "recall" => PackIntent.Recall,
        "thread" => PackIntent.Thread,
        _ => throw new ArgumentException($"unknown intent '{intent}' (expected goal_reasoning|portfolio|person_prep|recall|thread)"),
    };

    private static string IntentWire(PackIntent i) => i switch
    {
        PackIntent.GoalReasoning => "goal_reasoning",
        PackIntent.Portfolio => "portfolio",
        PackIntent.PersonPrep => "person_prep",
        PackIntent.Recall => "recall",
        PackIntent.Thread => "thread",
        _ => "recall",
    };

    private static MapObjectKind ParseKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "person" => MapObjectKind.Person,
        "thread" => MapObjectKind.Thread,
        "goal" => MapObjectKind.Goal,
        "timeline" => MapObjectKind.Timeline,
        _ => throw new ArgumentException($"unknown kind '{kind}' (expected person|thread|goal|timeline)"),
    };

    /// <summary>The POST /context-pack request body (design §5.1).</summary>
    public sealed record PackRequestBody(string? Intent, string? Focus, int? Budget, string? Since);

    // Wire DTOs — explicit snake_case shapes so the JSON matches design §2.1 exactly regardless of
    // the host's global JSON casing policy. `delta` / `disambiguation` serialise to null → omitted
    // by the default IgnoreNull policy configured on the pack route below.
    private sealed class PackWire
    {
        public string intent { get; set; } = "";
        public string? focus { get; set; }
        public int budget { get; set; }
        public int used { get; set; }
        public string as_of { get; set; } = "";
        public List<SectionWire> sections { get; set; } = new();
        public CoverageWire coverage { get; set; } = new();
        public List<OpenPointWire> open_points { get; set; } = new();
        public DeltaWire? delta { get; set; }
        public List<DisambiguationWire>? disambiguation { get; set; }
    }
    private sealed class SectionWire { public string section { get; set; } = ""; public List<ItemWire> items { get; set; } = new(); }
    private sealed class ItemWire { public string content { get; set; } = ""; public string source { get; set; } = ""; public double? confidence { get; set; } }
    private sealed class CoverageWire { public IReadOnlyList<string> looked_at { get; set; } = Array.Empty<string>(); public IReadOnlyList<string> deferred { get; set; } = Array.Empty<string>(); public IReadOnlyList<string> gaps { get; set; } = Array.Empty<string>(); }
    private sealed class OpenPointWire { public string point_id { get; set; } = ""; public string question { get; set; } = ""; public string kind { get; set; } = ""; }
    private sealed class DeltaWire { public string since { get; set; } = ""; public List<EvidenceWire> new_evidence { get; set; } = new(); public List<StatusChangeWire> status_changes { get; set; } = new(); }
    private sealed class EvidenceWire { public string date { get; set; } = ""; public string fact { get; set; } = ""; public string source { get; set; } = ""; }
    private sealed class StatusChangeWire { public string focus { get; set; } = ""; public string from { get; set; } = ""; public string to { get; set; } = ""; }
    private sealed class DisambiguationWire { public string candidate { get; set; } = ""; public string descriptor { get; set; } = ""; public int evidence_count { get; set; } }
}
