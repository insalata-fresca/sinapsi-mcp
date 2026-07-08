using System.Text;

namespace Cervello.Enrichment.Domain;

/// <summary>
/// A fully-assembled <c>map/</c> review-PR (DESIGN §5): the branch, title, body (carrying every
/// bundle back-link — R5), the mutations, and the stub declarations required by R4. Built + self-
/// linted by the graph-writer before it is handed to <see cref="Ports.IMapPrWriter"/>. Immutable.
/// </summary>
public sealed record MapReviewPr
{
    public MapReviewPr(
        string branch,
        string title,
        IReadOnlyList<MapMutation> mutations,
        IReadOnlyList<StubDeclaration> stubs,
        IReadOnlyList<string> bundleRefs)
    {
        if (string.IsNullOrWhiteSpace(branch))
            throw new ArgumentException("MapReviewPr.Branch must be non-empty", nameof(branch));
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("MapReviewPr.Title must be non-empty", nameof(title));
        ArgumentNullException.ThrowIfNull(mutations);
        if (mutations.Count == 0)
            throw new ArgumentException("a review-PR must carry >= 1 mutation", nameof(mutations));
        ArgumentNullException.ThrowIfNull(stubs);
        ArgumentNullException.ThrowIfNull(bundleRefs);
        if (bundleRefs.Count == 0)
            throw new ArgumentException("a review-PR body must back-link >= 1 bundle (lint R5)", nameof(bundleRefs));
        Branch = branch;
        Title = title;
        Mutations = mutations;
        Stubs = stubs;
        BundleRefs = bundleRefs;
    }

    public string Branch { get; }
    public string Title { get; }
    public IReadOnlyList<MapMutation> Mutations { get; }
    public IReadOnlyList<StubDeclaration> Stubs { get; }

    /// <summary>The bundle back-links (R5) in the PR body.</summary>
    public IReadOnlyList<string> BundleRefs { get; }

    /// <summary>The rendered PR body — every bundle back-link present (R5).</summary>
    public string RenderBody()
    {
        var sb = new StringBuilder();
        sb.Append("## Cervello graph-add (review-PR)\n\n");
        sb.Append("Grounded facts from the enrichment bundle(s) below. Every mutation carries its ");
        sb.Append("source + confidence + basis; nothing here is auto-merged (human gate).\n\n");
        sb.Append("**Bundles:**\n");
        foreach (var b in BundleRefs) sb.Append("- ").Append(b).Append('\n');
        if (Stubs.Count > 0)
        {
            sb.Append("\n**Declared stubs (R4):**\n");
            foreach (var s in Stubs) sb.Append("- ").Append(s.Path).Append(" (stub: true)\n");
        }
        sb.Append("\n**Mutations:**\n");
        foreach (var m in Mutations)
            sb.Append("- ").Append(m.DossierPath).Append(" ").Append(m.Section).Append(": ")
              .Append(m.Content).Append("  —  ").Append(m.BundleRef).Append('\n');
        return sb.ToString();
    }
}
