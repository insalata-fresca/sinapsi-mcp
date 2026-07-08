using System.Security.Cryptography;
using System.Text;
using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cervello.Enrichment.Pipeline;

/// <summary>
/// The engine behind <c>cervello_set_goal</c> (design §5.6) and <c>cervello_link_evidence</c>
/// (design §5.7) — the goal-object capture loop. Writes/updates the net-new <c>type: goal</c> dossier
/// (<c>map/goals/&lt;slug&gt;.md</c>, §3.1) and attaches evidence as sourced <c>## Movimento</c> lines,
/// BOTH through the graph-writer review-PR mechanism (lint-checked, dry-run by default — never
/// auto-merged; a human gate merges it).
///
/// <list type="bullet">
/// <item><b>Confirm-by-default (MC Q6).</b> <c>confirm=false</c> previews the exact dossier / line that
///   will be written (no PR); <c>confirm=true</c> opens the review-PR.</item>
/// <item><b>Never delete a prior grounded line (INGEST §5).</b> An update APPENDS to <c>## Movimento</c>
///   and revises <c>## Stato</c>/<c>status</c>; it never removes an existing sourced line.</item>
/// <item><b>Pin-on-cite (SCHEMAS §4 / LINT R11).</b> An external evidence ref (<c>drive://</c>/
///   <c>gmail://</c>) is pinned first by the graph-writer, so the merged line cites <c>pin://</c> — the
///   graph-writer already enforces this on every mutation source (reused verbatim).</item>
/// <item><b>Grounded floor.</b> No evidence line without a source ref (LINT R1) — the <c>evidence_ref</c>
///   is required and validated against the SCHEMAS §1 grammar.</item>
/// </list>
/// </summary>
public sealed class GoalService(
    IMapGraph graph,
    CervelloGraphWriter graphWriter,
    ILogger<GoalService>? logger = null)
{
    private readonly IMapGraph _graph = graph ?? throw new ArgumentNullException(nameof(graph));
    private readonly CervelloGraphWriter _writer = graphWriter ?? throw new ArgumentNullException(nameof(graphWriter));
    private readonly ILogger _log = logger ?? NullLogger<GoalService>.Instance;

    /// <summary><c>cervello_set_goal</c> (design §5.6): create/update a goal dossier via a review-PR.</summary>
    public async Task<GoalResult> SetGoalAsync(SetGoalRequest req, DateOnly today, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new ArgumentException("set_goal requires a name", nameof(req));
        var status = string.IsNullOrWhiteSpace(req.Status) ? "active" : req.Status!;
        GoalDossier.ValidateStatus(status); // fail-closed on an unknown status (MC Q2)

        var slug = Slugify(req.Name);
        var existing = await _graph.GetObjectAsync(MapObjectKind.Goal, slug, ct);
        var depositId = DepositId(slug, today);
        var basis = $"human://{depositId}";

        // Merge over the existing dossier: never delete a prior grounded ## Movimento line (INGEST §5).
        var priorMovimento = existing is null
            ? Array.Empty<TimelineLine>()
            : await _graph.WalkTimelineAsync($"goal:{slug}", null, null, ct);

        var dossier = new GoalDossier
        {
            Slug = slug,
            Name = req.Name.Trim(),
            Status = status,
            Horizon = req.Horizon,
            People = req.People ?? Array.Empty<string>(),
            Threads = req.Threads ?? Array.Empty<string>(),
            Tags = req.Tags ?? Array.Empty<string>(),
            Objective = req.Objective,
            ObjectiveSource = string.IsNullOrWhiteSpace(req.SourceHint) ? "" : $"deposit://{depositId}",
            Movimento = priorMovimento, // preserved — an update never deletes prior grounded lines
            NextSteps = req.NextSteps ?? Array.Empty<string>(),
            Updated = today.ToString("yyyy-MM-dd"),
        };

        var rendered = dossier.Render();
        var verb = existing is null ? "create" : "update";

        if (!req.Confirm)
        {
            _log.LogInformation("set_goal preview {Slug} ({Verb}, confirm=false — no PR)", slug, verb);
            return new GoalResult("preview", slug, null, dossier.Path, basis, Preview: rendered, Line: null, Source: null);
        }

        // Write the whole dossier as one mutation on the goal path (the graph-writer opens the review-PR).
        var mutation = new MapMutation(
            dossierPath: dossier.Path,
            section: "(dossier)",
            content: rendered,
            source: $"deposit://{depositId}",
            confidence: 1.0,
            bundleId: depositId,
            basisId: basis);
        var handle = await _writer.OpenReviewPrAsync(
            new GraphAddRequest(depositId, new[] { mutation }, Array.Empty<ReferencedLink>()), ct);

        _log.LogInformation("set_goal {Verb} {Slug} — review-PR {Branch}", verb, slug, handle?.Branch);
        return new GoalResult(verb == "create" ? "created" : "updated", slug, handle?.Branch, dossier.Path, basis, Preview: null, Line: null, Source: null);
    }

    /// <summary><c>cervello_link_evidence</c> (design §5.7): append a sourced <c>## Movimento</c> line to a goal.</summary>
    public async Task<GoalResult> LinkEvidenceAsync(LinkEvidenceRequest req, DateOnly today, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (string.IsNullOrWhiteSpace(req.GoalSlug))
            throw new ArgumentException("link_evidence requires a goal_slug", nameof(req));
        if (string.IsNullOrWhiteSpace(req.Fact))
            throw new ArgumentException("link_evidence requires a fact", nameof(req));
        // Grounded floor: no evidence line without a source ref (LINT R1).
        if (string.IsNullOrWhiteSpace(req.EvidenceRef) || !SourceRef.IsResolvableScheme(req.EvidenceRef))
            throw new ArgumentException("link_evidence requires a resolvable evidence_ref (rec://|pin://|drive://|gmail://|bundle://)", nameof(req));

        var slug = req.GoalSlug!.Trim();
        var existing = await _graph.GetObjectAsync(MapObjectKind.Goal, slug, ct);
        if (existing is null)
            return new GoalResult("unknown_goal", slug, null, $"map/goals/{slug}.md", Basis: null, Preview: null, Line: null, Source: null);

        var date = string.IsNullOrWhiteSpace(req.Date) ? today.ToString("yyyy-MM-dd") : req.Date!;
        var depositId = DepositId(slug + ":evidence", today);
        var basis = $"human://{depositId}";
        var line = GoalDossier.RenderMovimentoLine(new TimelineLine(date, req.Fact!.Trim(), new[] { slug }, req.EvidenceRef!));

        if (!req.Confirm)
        {
            _log.LogInformation("link_evidence preview {Slug} (confirm=false — no PR)", slug);
            return new GoalResult("preview", slug, null, $"map/goals/{slug}.md", basis, Preview: line, Line: line, Source: req.EvidenceRef);
        }

        // The graph-writer pins external refs on cite (R11) before authoring the merged line.
        var mutation = new MapMutation(
            dossierPath: $"map/goals/{slug}.md",
            section: "## Movimento",
            content: line,
            source: req.EvidenceRef!,
            confidence: 1.0,
            bundleId: depositId,
            basisId: basis);
        var handle = await _writer.OpenReviewPrAsync(
            new GraphAddRequest(depositId, new[] { mutation }, Array.Empty<ReferencedLink>()), ct);

        return new GoalResult("linked", slug, handle?.Branch, $"map/goals/{slug}.md", basis, Preview: null, Line: line, Source: req.EvidenceRef);
    }

    private static string Slugify(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (c is ' ' or '-' or '_' && sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().Trim('-') is { Length: > 0 } s ? s : "goal";
    }

    private static string DepositId(string seed, DateOnly today)
    {
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{today:yyyy-MM-dd}|{seed}"))).ToLowerInvariant();
        return $"{today:yyyyMMdd}-goal-{sha[..10]}";
    }
}

/// <summary>The <c>POST /goal</c> request (design §5.6).</summary>
public sealed record SetGoalRequest(
    string Name,
    string? Status = null,
    string? Horizon = null,
    string? Objective = null,
    IReadOnlyList<string>? People = null,
    IReadOnlyList<string>? Threads = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<string>? NextSteps = null,
    string? SourceHint = null,
    bool Confirm = false);

/// <summary>The <c>POST /goal/{slug}/evidence</c> request (design §5.7).</summary>
public sealed record LinkEvidenceRequest(
    string GoalSlug,
    string EvidenceRef,
    string Fact,
    string? Date = null,
    bool Confirm = false);

/// <summary>The result of a goal write (design §5.6/§5.7 responses).</summary>
public sealed record GoalResult(
    string Status,
    string GoalSlug,
    string? PrBranch,
    string Path,
    string? Basis,
    string? Preview,
    string? Line,
    string? Source);
