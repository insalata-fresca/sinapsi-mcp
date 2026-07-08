using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cervello.Enrichment.Bundles;

/// <summary>
/// Serializes an <see cref="EnrichmentBundle"/> to the SCHEMAS §6 <c>inbox/&lt;id&gt;/</c> pair
/// (<c>data.json</c> + human-readable <c>bundle.md</c>) and persists it via
/// <see cref="IBundleStore"/>. Before writing it runs a self-check that NO biometric/binary leaks
/// into the bundle (lint R7) — the bundle domain has no vector/binary member, but the writer also
/// scans the serialized JSON as a belt-and-braces guard. Written once per bundle id.
/// </summary>
public sealed class BundleWriter(IBundleStore store, ILogger<BundleWriter>? logger = null)
{
    private readonly IBundleStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ILogger _log = logger ?? NullLogger<BundleWriter>.Instance;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Render the SCHEMAS §6 <c>data.json</c> for a bundle.</summary>
    public static string RenderDataJson(EnrichmentBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var dto = new BundleJson(
            bundle.BundleId, bundle.SourceRef, bundle.IdempotencyKey, bundle.Kind,
            bundle.CreatedAt, bundle.State,
            new EnrichmentJson(
                bundle.Enrichment.Summary,
                bundle.Enrichment.Entities,
                bundle.Enrichment.Dates,
                bundle.Enrichment.ProposedLinks.Select(l => new LinkJson(l.Target, l.Confidence)).ToList(),
                bundle.Enrichment.ProposedTimeline.Select(t => new TimelineJson(t.Date, t.Fact, t.Source)).ToList(),
                bundle.Enrichment.Attribution
                    .Select(a => new AttributionJson(a.Segment, a.Candidate, a.Confidence, a.Status, null))
                    .ToList()),
            new AttentionJson(bundle.Attention.Verdict, bundle.Attention.Score, bundle.Attention.Reason));
        return JsonSerializer.Serialize(dto, JsonOpts);
    }

    /// <summary>Render the human-readable <c>bundle.md</c> (summary + proposed lines + links).</summary>
    public static string RenderBundleMd(EnrichmentBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var sb = new StringBuilder();
        sb.Append("# Bundle ").Append(bundle.BundleId).Append('\n');
        sb.Append("\n<!-- ").Append(bundle.BundleRef).Append(" -->\n"); // R5 back-link marker
        sb.Append("\n**Source:** ").Append(bundle.SourceRef).Append('\n');
        sb.Append("**Kind:** ").Append(bundle.Kind).Append("  ");
        sb.Append("**State:** ").Append(bundle.State).Append('\n');
        sb.Append("\n## Summary\n\n").Append(bundle.Enrichment.Summary).Append('\n');

        if (bundle.Enrichment.Dates.Count > 0)
            sb.Append("\n## Dates\n\n- ").Append(string.Join("\n- ", bundle.Enrichment.Dates)).Append('\n');

        if (bundle.Enrichment.ProposedLinks.Count > 0)
        {
            sb.Append("\n## Proposed links\n\n");
            foreach (var l in bundle.Enrichment.ProposedLinks)
                sb.Append("- ").Append(l.Target).Append(" (").Append(l.Confidence.ToString("0.##")).Append(")\n");
        }

        if (bundle.Enrichment.ProposedTimeline.Count > 0)
        {
            sb.Append("\n## Proposed timeline\n\n");
            foreach (var t in bundle.Enrichment.ProposedTimeline)
                sb.Append(t.ToTimelineLine()).Append('\n'); // each carries source: (R1)
        }

        if (bundle.Enrichment.Attribution.Count > 0)
        {
            sb.Append("\n## Attribution (needs confirmation)\n\n");
            foreach (var a in bundle.Enrichment.Attribution)
                sb.Append("- ").Append(a.Segment).Append(" → ").Append(a.Candidate)
                  .Append(" (").Append(a.Confidence.ToString("0.##")).Append(", ").Append(a.Status).Append(")\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Write the bundle to <c>inbox/&lt;id&gt;/</c>. Idempotent: if the bundle already exists it is
    /// a logged no-op. Runs the R7 no-binary self-check before persisting.
    /// </summary>
    public async Task<BundleWriteResult> WriteAsync(EnrichmentBundle bundle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        if (await _store.ExistsAsync(bundle.BundleId, ct).ConfigureAwait(false))
        {
            _log.LogInformation("bundle write no-op {Id}: already exists", bundle.BundleId);
            return new BundleWriteResult(_store.BundlePath(bundle.BundleId, ""), AlreadyExisted: true);
        }

        var dataJson = RenderDataJson(bundle);
        var bundleMd = RenderBundleMd(bundle);

        // Lint R7 self-check (graph-writer / bundle-writer self-check before persisting).
        BundleGuard.EnsureNoBinaries(dataJson);
        BundleGuard.EnsureNoBinaries(bundleMd);

        var dir = await _store.WriteAsync(bundle.BundleId, dataJson, bundleMd, ct).ConfigureAwait(false);
        _log.LogInformation("bundle written {Id} → {Dir}", bundle.BundleId, dir);
        return new BundleWriteResult(dir, AlreadyExisted: false);
    }

    // ── SCHEMAS §6 data.json DTOs (snake_case) ──────────────────────────────────────────────────
    private sealed record BundleJson(
        string BundleId, string SourceRef, string IdempotencyKey, string Kind,
        string CreatedAt, string State, EnrichmentJson Enrichment, AttentionJson Attention);

    private sealed record EnrichmentJson(
        string Summary, IReadOnlyList<string> Entities, IReadOnlyList<string> Dates,
        IReadOnlyList<LinkJson> ProposedLinks, IReadOnlyList<TimelineJson> ProposedTimeline,
        IReadOnlyList<AttributionJson> Attribution);

    private sealed record LinkJson(string Target, double Confidence);
    private sealed record TimelineJson(string Date, string Fact, string Source);
    private sealed record AttributionJson(string Segment, string Candidate, double Confidence, string Status, object? Basis);
    private sealed record AttentionJson(string Verdict, double Score, string Reason);
}

/// <summary>Outcome of a bundle write.</summary>
public sealed record BundleWriteResult(string InboxDir, bool AlreadyExisted);
