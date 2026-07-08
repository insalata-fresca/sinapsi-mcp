namespace Cervello.Enrichment.Domain;

/// <summary>
/// One mutation the graph-writer proposes to <c>map/</c> (DESIGN §5; a review-PR section). Each
/// mutation carries its provenance so lint passes: a <c>source:</c> ref, a confidence, a
/// confirmation <c>basis</c> id (for an attribution — lint R9), and the <c>bundle://</c> back-link
/// (lint R5). Timeline mutations additionally supply the SCHEMAS §4 line (which carries the
/// <c>source:</c> for R1). External evidence in a MERGED map line must be pinned (R11) — the
/// writer enforces this on <see cref="Source"/> before authoring.
/// </summary>
public sealed record MapMutation
{
    public MapMutation(
        string dossierPath,
        string section,
        string content,
        string source,
        double confidence,
        string bundleId,
        string? basisId = null)
    {
        if (string.IsNullOrWhiteSpace(dossierPath))
            throw new ArgumentException("MapMutation.DossierPath must be non-empty", nameof(dossierPath));
        if (string.IsNullOrWhiteSpace(section))
            throw new ArgumentException("MapMutation.Section must be non-empty", nameof(section));
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("MapMutation.Content must be non-empty", nameof(content));
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("MapMutation.Source must be non-empty (lint R1/R2)", nameof(source));
        if (string.IsNullOrWhiteSpace(bundleId))
            throw new ArgumentException("MapMutation.BundleId must be non-empty (lint R5)", nameof(bundleId));
        DossierPath = dossierPath;
        Section = section;
        Content = content;
        Source = source;
        Confidence = confidence;
        BundleId = bundleId;
        BasisId = basisId;
    }

    /// <summary>The repo-relative dossier file this mutation touches (e.g. <c>map/people/guilhem.md</c>).</summary>
    public string DossierPath { get; }

    /// <summary>The section heading within the dossier (e.g. <c>## Timeline</c>).</summary>
    public string Section { get; }

    /// <summary>The line/content added (a timeline line, a dossier note).</summary>
    public string Content { get; }

    /// <summary>The primary <c>source:</c> ref backing this mutation (R1/R2/R11).</summary>
    public string Source { get; }

    public double Confidence { get; }

    /// <summary>The bundle id this mutation traces to (R5).</summary>
    public string BundleId { get; }

    /// <summary>The confirmation basis id (R9) — required for an attribution mutation.</summary>
    public string? BasisId { get; }

    public string BundleRef => $"bundle://{BundleId}";
}

/// <summary>
/// A dossier stub the same PR must declare when a proposed <c>[[link]]</c> targets a non-existent
/// dossier (lint R4): a minimal <c>type: person|thread</c> file with <c>stub: true</c>.
/// </summary>
public sealed record StubDeclaration
{
    public StubDeclaration(string slug, string type)
    {
        if (string.IsNullOrWhiteSpace(slug))
            throw new ArgumentException("StubDeclaration.Slug must be non-empty", nameof(slug));
        if (type is not ("person" or "thread" or "project"))
            throw new ArgumentException($"stub type must be person|thread|project (got '{type}')", nameof(type));
        Slug = slug;
        Type = type;
    }

    public string Slug { get; }
    public string Type { get; }

    /// <summary>The repo-relative path the stub file is authored at.</summary>
    public string Path => Type switch
    {
        "person" => $"map/people/{Slug}.md",
        "thread" => $"map/threads/{Slug}.md",
        _ => $"map/projects/{Slug}.md",
    };

    /// <summary>The minimal stub frontmatter (SCHEMAS §2/§3; <c>stub: true</c> satisfies R4).</summary>
    public string RenderStub(string updated) =>
        $"---\ntype: {Type}\nname: {Slug}\nstub: true\nupdated: {updated}\n---\n";
}
