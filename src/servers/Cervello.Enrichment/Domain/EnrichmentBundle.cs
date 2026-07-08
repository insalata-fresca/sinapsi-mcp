namespace Cervello.Enrichment.Domain;

/// <summary>
/// The enrichment bundle exactly as SCHEMAS §6 declares it (<c>inbox/&lt;id&gt;/data.json</c>). A
/// bundle only ever *proposes* — it carries proposed links + timeline lines + attribution
/// entries, none of them applied to <c>map/</c>. Attribution entries are ALWAYS
/// <c>needs_confirmation</c> with <c>basis: null</c> at bundle stage (SCHEMAS §6; the decision
/// policy runs later, at apply). The bundle MUST NOT contain audio, embeddings, or raw mail
/// bodies (lint R6/R7) — enforced by construction (there is no vector/binary member here).
/// </summary>
public sealed record EnrichmentBundle
{
    public EnrichmentBundle(
        string bundleId,
        string sourceRef,
        string idempotencyKey,
        string kind,
        string createdAt,
        string state,
        BundleEnrichment enrichment,
        BundleAttention attention)
    {
        if (string.IsNullOrWhiteSpace(bundleId))
            throw new ArgumentException("bundle_id must be non-empty", nameof(bundleId));
        if (string.IsNullOrWhiteSpace(sourceRef))
            throw new ArgumentException("source_ref must be non-empty", nameof(sourceRef));
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("idempotency_key must be non-empty", nameof(idempotencyKey));
        if (string.IsNullOrWhiteSpace(kind))
            throw new ArgumentException("kind must be non-empty", nameof(kind));
        ArgumentNullException.ThrowIfNull(enrichment);
        ArgumentNullException.ThrowIfNull(attention);
        BundleId = bundleId;
        SourceRef = sourceRef;
        IdempotencyKey = idempotencyKey;
        Kind = kind;
        CreatedAt = createdAt;
        State = state;
        Enrichment = enrichment;
        Attention = attention;
    }

    public string BundleId { get; }
    public string SourceRef { get; }
    public string IdempotencyKey { get; }

    /// <summary><c>recording | document | mail_thread | deposit</c>.</summary>
    public string Kind { get; }
    public string CreatedAt { get; }
    public string State { get; }
    public BundleEnrichment Enrichment { get; }
    public BundleAttention Attention { get; }

    /// <summary>The self-referential back-link ref (lint R5) for this bundle.</summary>
    public string BundleRef => $"bundle://{BundleId}";
}

/// <summary>The <c>enrichment</c> block of SCHEMAS §6.</summary>
public sealed record BundleEnrichment
{
    public BundleEnrichment(
        string summary,
        IReadOnlyList<string> entities,
        IReadOnlyList<string> dates,
        IReadOnlyList<ProposedLink> proposedLinks,
        IReadOnlyList<ProposedTimelineLine> proposedTimeline,
        IReadOnlyList<BundleAttribution> attribution)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(dates);
        ArgumentNullException.ThrowIfNull(proposedLinks);
        ArgumentNullException.ThrowIfNull(proposedTimeline);
        ArgumentNullException.ThrowIfNull(attribution);
        Summary = summary;
        Entities = entities;
        Dates = dates;
        ProposedLinks = proposedLinks;
        ProposedTimeline = proposedTimeline;
        Attribution = attribution;
    }

    public string Summary { get; }
    public IReadOnlyList<string> Entities { get; }
    public IReadOnlyList<string> Dates { get; }
    public IReadOnlyList<ProposedLink> ProposedLinks { get; }
    public IReadOnlyList<ProposedTimelineLine> ProposedTimeline { get; }
    public IReadOnlyList<BundleAttribution> Attribution { get; }
}

/// <summary>A proposed <c>[[slug]]</c> link with confidence (SCHEMAS §6).</summary>
public sealed record ProposedLink
{
    public ProposedLink(string target, double confidence)
    {
        if (string.IsNullOrWhiteSpace(target))
            throw new ArgumentException("proposed_link target must be non-empty", nameof(target));
        if (!(target.StartsWith("[[", StringComparison.Ordinal) && target.EndsWith("]]", StringComparison.Ordinal)))
            throw new ArgumentException($"proposed_link target must be a [[slug]] wikilink (got '{target}')", nameof(target));
        if (confidence is < 0.0 or > 1.0)
            throw new ArgumentOutOfRangeException(nameof(confidence), confidence, "confidence must be in [0,1]");
        Target = target;
        Confidence = confidence;
    }

    public string Target { get; }
    public double Confidence { get; }

    /// <summary>The bare slug inside the <c>[[...]]</c>.</summary>
    public string Slug => Target[2..^2];
}

/// <summary>
/// A proposed timeline line (SCHEMAS §4/§6). Every line MUST carry a valid <c>source:</c> ref
/// (lint R1) — enforced in the constructor so an unsourced timeline line cannot exist.
/// </summary>
public sealed record ProposedTimelineLine
{
    public ProposedTimelineLine(string date, string fact, string source)
    {
        if (string.IsNullOrWhiteSpace(date))
            throw new ArgumentException("timeline date must be non-empty", nameof(date));
        if (string.IsNullOrWhiteSpace(fact))
            throw new ArgumentException("timeline fact must be non-empty", nameof(fact));
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException(
                "a timeline line MUST carry a source: ref (lint R1) — an unsourced line is never proposed",
                nameof(source));
        Date = date;
        Fact = fact;
        Source = source;
    }

    public string Date { get; }
    public string Fact { get; }
    public string Source { get; }

    /// <summary>The SCHEMAS §4 wire form: <c>- YYYY-MM-DD — &lt;fact&gt; — source: &lt;ref&gt;</c>.</summary>
    public string ToTimelineLine() => $"- {Date} — {Fact} — source: {Source}";
}

/// <summary>
/// A bundle-stage attribution entry (SCHEMAS §6). ALWAYS <c>needs_confirmation</c> with
/// <c>basis: null</c> — a bundle only proposes; the decision policy runs at apply.
/// </summary>
public sealed record BundleAttribution
{
    public BundleAttribution(string segment, string candidate, double confidence)
    {
        if (string.IsNullOrWhiteSpace(segment))
            throw new ArgumentException("attribution segment must be non-empty", nameof(segment));
        if (string.IsNullOrWhiteSpace(candidate))
            throw new ArgumentException("attribution candidate must be non-empty", nameof(candidate));
        if (confidence is < 0.0 or > 1.0)
            throw new ArgumentOutOfRangeException(nameof(confidence), confidence, "confidence must be in [0,1]");
        Segment = segment;
        Candidate = candidate;
        Confidence = confidence;
    }

    public string Segment { get; }
    public string Candidate { get; }
    public double Confidence { get; }

    /// <summary>SCHEMAS §6: always <c>needs_confirmation</c> at bundle stage.</summary>
    public string Status => "needs_confirmation";

    /// <summary>SCHEMAS §6: always null at bundle stage (basis is set at apply/resolution).</summary>
    public object? Basis => null;
}

/// <summary>The <c>attention</c> block of SCHEMAS §6.</summary>
public sealed record BundleAttention
{
    public BundleAttention(string verdict, double score, string reason)
    {
        if (verdict is not ("discard" or "ping" or "promote"))
            throw new ArgumentException($"attention verdict must be discard|ping|promote (got '{verdict}')", nameof(verdict));
        Verdict = verdict;
        Score = score;
        Reason = reason ?? "";
    }

    /// <summary>A promoted item — this bundle exists because it was promoted.</summary>
    public static BundleAttention Promote(double score, string reason) => new("promote", score, reason);

    public string Verdict { get; }
    public double Score { get; }
    public string Reason { get; }
}
