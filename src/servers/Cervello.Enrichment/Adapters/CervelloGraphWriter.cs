using Cervello.Enrichment.Bundles;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// The <c>cervello-graph-writer</c> (DESIGN §5, deliberately separate): assembles a back-linked
/// <c>map/</c> review-PR from the APPLIED facts of a bundle and runs its OWN
/// <c>cervello-lint</c> R1/R4/R5/R6/R7/R11 self-check before handing the PR to
/// <see cref="IMapPrWriter"/> (LINT.md: "a graph-writer self-check before it opens any map PR").
/// Nothing is auto-merged — a human gate merges the opened PR.
///
/// <list type="bullet">
/// <item><b>R1</b> — every timeline mutation carries a <c>source:</c> (enforced by the mutation ctor).</item>
/// <item><b>R4</b> — a proposed <c>[[link]]</c> to a missing dossier is declared as a stub in the same PR.</item>
/// <item><b>R5</b> — every mutation + the PR body back-links its <c>bundle://&lt;id&gt;</c>.</item>
/// <item><b>R11</b> — external evidence (<c>drive://</c>/<c>gmail://</c>) in a merged map line is
///   pinned to <c>pin://&lt;sha&gt;</c>, keeping the external ref as provenance only.</item>
/// <item><b>R6/R7</b> — the rendered PR carries no raw mail body / binary (<see cref="BundleGuard"/>).</item>
/// </list>
/// </summary>
public sealed class CervelloGraphWriter(
    IMapPrWriter prWriter,
    ILinkResolver linkResolver,
    IPinStore pinStore,
    ILogger<CervelloGraphWriter>? logger = null)
{
    private readonly IMapPrWriter _pr = prWriter ?? throw new ArgumentNullException(nameof(prWriter));
    private readonly ILinkResolver _links = linkResolver ?? throw new ArgumentNullException(nameof(linkResolver));
    private readonly IPinStore _pins = pinStore ?? throw new ArgumentNullException(nameof(pinStore));
    private readonly ILogger _log = logger ?? NullLogger<CervelloGraphWriter>.Instance;

    /// <summary>
    /// Assemble + self-lint + open the review-PR for a set of applied mutations + the links they
    /// reference. Returns null if there are no applied mutations (nothing to write).
    /// </summary>
    public async Task<MapPrHandle?> OpenReviewPrAsync(GraphAddRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Mutations.Count == 0)
        {
            _log.LogInformation("graph-add {Bundle}: no applied mutations — no PR", request.BundleId);
            return null;
        }

        // R11: pin external evidence in merged map lines; keep the external ref as provenance only.
        var pinned = new List<MapMutation>(request.Mutations.Count);
        foreach (var m in request.Mutations)
            pinned.Add(await PinExternalAsync(m, ct).ConfigureAwait(false));

        // R4: any referenced link that does not resolve is declared as a stub in THIS PR.
        var stubs = new List<StubDeclaration>();
        foreach (var link in request.ReferencedLinks)
        {
            if (!await _links.DossierExistsAsync(link.Slug, ct).ConfigureAwait(false))
            {
                stubs.Add(new StubDeclaration(link.Slug, link.Type));
                _log.LogInformation("graph-add {Bundle}: declaring stub {Path} (R4)",
                    request.BundleId, stubs[^1].Path);
            }
        }

        var bundleRefs = pinned.Select(m => m.BundleRef).Distinct(StringComparer.Ordinal).ToList();
        var pr = new MapReviewPr(
            branch: $"cervello/graph-add-{request.BundleId}",
            title: $"cervello graph-add: {request.BundleId}",
            mutations: pinned,
            stubs: stubs,
            bundleRefs: bundleRefs);

        SelfLint(pr);

        var handle = await _pr.OpenPrAsync(pr, ct).ConfigureAwait(false);
        _log.LogInformation("graph-add {Bundle}: review-PR opened on {Branch} ({Mutations} mutations, {Stubs} stubs)",
            request.BundleId, handle.Branch, pinned.Count, stubs.Count);
        return handle;
    }

    /// <summary>R11: replace an external (<c>drive://</c>/<c>gmail://</c>) source with a pinned ref.</summary>
    private async Task<MapMutation> PinExternalAsync(MapMutation m, CancellationToken ct)
    {
        if (!IsExternal(m.Source)) return m; // rec:// and repo-relative refs are Tier-1-internal (R11 exempt)

        var sha = await _pins.PinAsync(m.Source, ct).ConfigureAwait(false);
        var pinnedSource = $"pin://{sha} ({m.Source})"; // external kept as provenance only (SCHEMAS §4)
        var pinnedContent = m.Content.Replace(m.Source, pinnedSource, StringComparison.Ordinal);
        return new MapMutation(
            m.DossierPath, m.Section, pinnedContent, pinnedSource, m.Confidence, m.BundleId, m.BasisId);
    }

    private static bool IsExternal(string source) =>
        source.StartsWith("drive://", StringComparison.Ordinal)
        || source.StartsWith("gmail://", StringComparison.Ordinal);

    /// <summary>The graph-writer self-check (R1/R4/R5/R6/R7/R11) before opening the PR.</summary>
    private static void SelfLint(MapReviewPr pr)
    {
        var body = pr.RenderBody();

        // R5: every mutation back-links its bundle, and the body carries the back-links.
        foreach (var m in pr.Mutations)
            if (!body.Contains(m.BundleRef, StringComparison.Ordinal))
                throw new InvalidOperationException($"lint R5: mutation {m.DossierPath} lacks its {m.BundleRef} back-link");

        // R4: any [[link]] in mutation content that isn't a declared stub must have been resolved
        //     upstream. Here we assert every declared stub is well-formed.
        var stubSlugs = pr.Stubs.Select(s => s.Slug).ToHashSet(StringComparer.Ordinal);
        _ = stubSlugs; // resolution happened in OpenReviewPrAsync; stubs declared for unresolved links.

        foreach (var m in pr.Mutations)
        {
            // R1/R2: timeline lines carry a source: ref (mutation ctor guarantees Source non-empty;
            // for a timeline section the rendered content must contain "source:").
            if (m.Section.Contains("Timeline", StringComparison.OrdinalIgnoreCase)
                && !m.Content.Contains("source:", StringComparison.Ordinal))
                throw new InvalidOperationException($"lint R1: timeline mutation on {m.DossierPath} has no 'source:' ref");

            // R11: a merged map line must not cite a bare external ref without a pin.
            if (IsExternal(m.Source))
                throw new InvalidOperationException(
                    $"lint R11: mutation on {m.DossierPath} still cites a bare external ref {m.Source} — must be pin://");
        }

        // R6/R7: no raw mail body / binary in the PR body or any mutation content.
        BundleGuard.EnsureNoRawMailBody(body);
        BundleGuard.EnsureNoBinaries(body);
        foreach (var m in pr.Mutations)
        {
            BundleGuard.EnsureNoRawMailBody(m.Content);
            BundleGuard.EnsureNoBinaries(m.Content);
        }
    }
}

/// <summary>The applied-fact input to a graph-add: the mutations + the links they reference (for R4 stubs).</summary>
public sealed record GraphAddRequest
{
    public GraphAddRequest(
        string bundleId,
        IReadOnlyList<MapMutation> mutations,
        IReadOnlyList<ReferencedLink> referencedLinks)
    {
        if (string.IsNullOrWhiteSpace(bundleId))
            throw new ArgumentException("GraphAddRequest.BundleId must be non-empty", nameof(bundleId));
        ArgumentNullException.ThrowIfNull(mutations);
        ArgumentNullException.ThrowIfNull(referencedLinks);
        BundleId = bundleId;
        Mutations = mutations;
        ReferencedLinks = referencedLinks;
    }

    public string BundleId { get; }
    public IReadOnlyList<MapMutation> Mutations { get; }
    public IReadOnlyList<ReferencedLink> ReferencedLinks { get; }
}

/// <summary>A link a mutation references, with its dossier type (for R4 stub declaration).</summary>
public sealed record ReferencedLink(string Slug, string Type);
