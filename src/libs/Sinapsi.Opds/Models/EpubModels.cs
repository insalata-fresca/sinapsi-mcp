// ---------------------------------------------------------------------------
// ExtractedBook - the structured, clean-text output of EpubExtractor.
//
// An EPUB is zip { mimetype, META-INF/container.xml -> OPF (manifest+spine) ->
// xhtml }. The extractor walks the spine in order, cleans each xhtml document to
// text, and splits it into sections at heading boundaries (<h1..h6>), retaining
// the book -> chapter -> section hierarchy that M3's chunker + M4's scanner
// consume. Headings come from the xhtml itself, cross-referenced with the
// nav/toc (EPUB3 nav.xhtml or EPUB2 toc.ncx) for the chapter title.
//
// Plain-ASCII banner so this source diffs as TEXT, never binary.
// ---------------------------------------------------------------------------

namespace Sinapsi.Opds.Models;

/// <summary>
/// One section within a chapter: the text under a single heading (and above the
/// next same-or-higher heading). <see cref="Heading"/> is null for the lead-in
/// text before a chapter's first heading. Text is clean (scripts/styles stripped,
/// entities decoded, paragraph boundaries preserved as blank-line separators).
/// </summary>
public sealed record ExtractedSection(string? Heading, int HeadingLevel, string Text);

/// <summary>
/// One chapter = one spine item (xhtml document). <see cref="Order"/> is its
/// 0-based position in the spine; <see cref="Heading"/> is the chapter title
/// (from the toc/nav, else the document's first heading, else the &lt;title&gt;);
/// <see cref="Href"/> is the spine item's path inside the EPUB (a stable anchor
/// for citations); <see cref="Sections"/> is the heading-split body.
/// </summary>
public sealed record ExtractedChapter(
    int Order,
    string? Heading,
    string Href,
    IReadOnlyList<ExtractedSection> Sections)
{
    /// <summary>The whole chapter's clean text, sections joined with blank lines.
    /// Convenience for callers that want chapter-level text rather than sections.</summary>
    public string Text => string.Join("\n\n", Sections.Select(s => s.Text).Where(t => t.Length > 0));
}

/// <summary>
/// The extracted book: metadata from the OPF plus chapters in spine order. All
/// list members are non-null (empty, never null).
/// </summary>
public sealed record ExtractedBook
{
    /// <summary>OPF <c>dc:title</c>, when present.</summary>
    public string? Title { get; init; }

    /// <summary>OPF <c>dc:creator</c> values.</summary>
    public IReadOnlyList<string> Authors { get; init; } = Array.Empty<string>();

    /// <summary>OPF <c>dc:language</c>, when present.</summary>
    public string? Language { get; init; }

    /// <summary>OPF <c>dc:identifier</c> (ISBN/URN), when present.</summary>
    public string? Identifier { get; init; }

    /// <summary>Chapters in spine order.</summary>
    public IReadOnlyList<ExtractedChapter> Chapters { get; init; } = Array.Empty<ExtractedChapter>();

    /// <summary>The whole book's clean text, chapters joined with blank lines.</summary>
    public string Text => string.Join("\n\n", Chapters.Select(c => c.Text).Where(t => t.Length > 0));
}
