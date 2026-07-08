using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cervello.Enrichment.Pipeline.Stages;

/// <summary>
/// The enrich+link stage (spec <c>enrichment-linking</c> → "Produce the enrichment bundle";
/// DESIGN §5.2 step 4). Given a recording's derived facts — summary, entities, dates, proposed
/// <c>[[person]]</c>/<c>[[thread]]</c>/<c>[[project]]</c> links, proposed timeline lines, and the
/// attribution verdicts from <see cref="AttributionStage"/> — it assembles an
/// <see cref="EnrichmentBundle"/> of PROPOSED facts.
///
/// <para>Grounding invariants it enforces:
/// <list type="bullet">
/// <item>every timeline line carries a valid <c>source:</c> ref (lint R1) — the
///   <see cref="ProposedTimelineLine"/> constructor rejects an unsourced line, so an unsourced
///   line cannot reach the bundle;</item>
/// <item>attribution entries are ALWAYS <c>needs_confirmation</c> with <c>basis: null</c> at bundle
///   stage (SCHEMAS §6) — the applied verdict is projected down to a bare candidate here;</item>
/// <item>no audio/embeddings — only slugs, confidences, refs cross into the bundle (lint R7).</item>
/// </list></para>
///
/// <para>Link resolution for R4 is recorded per-link (resolves vs needs-stub) but the stub file is
/// declared by the APPLY stage's PR — the bundle only proposes.</para>
/// </summary>
public sealed class EnrichLinkStage(ILinkResolver linkResolver, ILogger<EnrichLinkStage>? logger = null)
{
    private readonly ILinkResolver _links = linkResolver ?? throw new ArgumentNullException(nameof(linkResolver));
    private readonly ILogger _log = logger ?? NullLogger<EnrichLinkStage>.Instance;

    /// <summary>Assemble the enrichment bundle for a recording.</summary>
    public async Task<EnrichmentBundle> EnrichAsync(
        EnrichLinkInput input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Project each applied/attribution verdict down to a bundle-stage entry: candidate +
        // confidence, ALWAYS needs_confirmation / basis null (SCHEMAS §6). An omitted verdict
        // (unidentified speaker) is NOT proposed as an attribution — never guess a name.
        var attribution = new List<BundleAttribution>();
        foreach (var v in input.Attribution)
        {
            if (v.Outcome == AttributionOutcome.Omitted) continue; // unidentified → not a proposed name
            var candidate = v.Person ?? FirstCandidate(v);
            if (candidate is null) continue; // an open-point with no named candidate proposes nothing
            attribution.Add(new BundleAttribution(v.MergedSpeaker, candidate, v.Confidence));
        }

        // Resolve links for R4 bookkeeping (the stub is authored at apply, not here).
        var links = new List<ProposedLink>(input.ProposedLinks.Count);
        foreach (var link in input.ProposedLinks)
        {
            var exists = await _links.DossierExistsAsync(link.Slug, ct).ConfigureAwait(false);
            if (!exists)
                _log.LogInformation("bundle {Id}: proposed link {Target} needs a stub at apply (R4)",
                    input.BundleId, link.Target);
            links.Add(link);
        }

        var enrichment = new BundleEnrichment(
            summary: input.Summary,
            entities: input.Entities,
            dates: input.Dates,
            proposedLinks: links,
            proposedTimeline: input.ProposedTimeline,
            attribution: attribution);

        var bundle = new EnrichmentBundle(
            bundleId: input.BundleId,
            sourceRef: input.SourceRef,
            idempotencyKey: input.IdempotencyKey,
            kind: input.Kind,
            createdAt: input.CreatedAt,
            state: EnrichmentStateMachine.Name(EnrichmentState.BundleCreated),
            enrichment: enrichment,
            attention: input.Attention);

        _log.LogInformation("bundle {Id}: {Links} links, {Timeline} timeline, {Attr} attributions (all needs_confirmation)",
            input.BundleId, links.Count, input.ProposedTimeline.Count, attribution.Count);
        return bundle;
    }

    private static string? FirstCandidate(AttributionVerdict v) =>
        v.Person; // open-point/omitted carry no named person; only applied verdicts name one
}

/// <summary>The input facts the enrich+link stage assembles into a bundle.</summary>
public sealed record EnrichLinkInput
{
    public EnrichLinkInput(
        string bundleId,
        string sourceRef,
        string idempotencyKey,
        string kind,
        string createdAt,
        string summary,
        IReadOnlyList<string> entities,
        IReadOnlyList<string> dates,
        IReadOnlyList<ProposedLink> proposedLinks,
        IReadOnlyList<ProposedTimelineLine> proposedTimeline,
        IReadOnlyList<AttributionVerdict> attribution,
        BundleAttention attention)
    {
        BundleId = bundleId;
        SourceRef = sourceRef;
        IdempotencyKey = idempotencyKey;
        Kind = kind;
        CreatedAt = createdAt;
        Summary = summary ?? "";
        Entities = entities ?? Array.Empty<string>();
        Dates = dates ?? Array.Empty<string>();
        ProposedLinks = proposedLinks ?? Array.Empty<ProposedLink>();
        ProposedTimeline = proposedTimeline ?? Array.Empty<ProposedTimelineLine>();
        Attribution = attribution ?? Array.Empty<AttributionVerdict>();
        Attention = attention;
    }

    public string BundleId { get; }
    public string SourceRef { get; }
    public string IdempotencyKey { get; }
    public string Kind { get; }
    public string CreatedAt { get; }
    public string Summary { get; }
    public IReadOnlyList<string> Entities { get; }
    public IReadOnlyList<string> Dates { get; }
    public IReadOnlyList<ProposedLink> ProposedLinks { get; }
    public IReadOnlyList<ProposedTimelineLine> ProposedTimeline { get; }
    public IReadOnlyList<AttributionVerdict> Attribution { get; }
    public BundleAttention Attention { get; }
}
