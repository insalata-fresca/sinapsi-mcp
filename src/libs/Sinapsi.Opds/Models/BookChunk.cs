// ---------------------------------------------------------------------------
// BookChunk - the output unit of BookChunker: one retrievable, embeddable slice
// of a book, carrying its book -> chapter -> section hierarchy + OPDS facets so
// small-to-big expansion and facet-scoped retrieval (design doc SS3.4/SS3.5) work
// without a join back to the source book.
//
// Plain-ASCII banner so this source diffs as TEXT, never binary.
// ---------------------------------------------------------------------------

namespace Sinapsi.Opds.Models;

/// <summary>
/// One chunk of a book: ~300-500 words of contiguous text (see
/// <see cref="Sinapsi.Opds.BookChunker"/>), keyed <c>book:&lt;isbn-or-id&gt;:
/// &lt;chapterOrder&gt;:&lt;chunkIndex&gt;</c>, with the chapter/section headings
/// and the book-level facets (categories/authors/isbn/title) carried on every
/// chunk so a search hit is self-describing and a small-to-big expansion can
/// walk back up the hierarchy without re-fetching the book.
/// </summary>
public sealed record BookChunk
{
    /// <summary>Stable key: <c>book:&lt;isbn-or-id&gt;:&lt;chapterOrder&gt;:&lt;chunkIndex&gt;</c>.
    /// <c>isbn-or-id</c> is <see cref="Isbn"/> when present, else a caller-supplied
    /// fallback id (see <see cref="Sinapsi.Opds.BookChunker.Chunk"/>).</summary>
    public required string Key { get; init; }

    /// <summary>The chunk's clean text (~300-500 words, ~15% overlap with neighbors).</summary>
    public required string Text { get; init; }

    /// <summary>0-based chapter position (== <c>ExtractedChapter.Order</c>).</summary>
    public required int ChapterOrder { get; init; }

    /// <summary>The chapter's heading/title, when known. Carried onto every chunk
    /// in the chapter so a hit is self-describing without a parent lookup.</summary>
    public string? ChapterHeading { get; init; }

    /// <summary>The heading of the section this chunk's text was drawn from
    /// (the nearest enclosing <c>ExtractedSection.Heading</c>), when known.</summary>
    public string? SectionHeading { get; init; }

    /// <summary>0-based position of this chunk within its chapter (chunks are
    /// numbered per-chapter, not per-book — matches the key format).</summary>
    public required int ChunkIndex { get; init; }

    /// <summary>BookLore / OPDS category facets, carried from the source
    /// <c>OpdsEntry.Categories</c> onto every chunk (design doc SS3.5).</summary>
    public IReadOnlyList<string> Categories { get; init; } = Array.Empty<string>();

    /// <summary>The book's ISBN/identifier (from <c>OpdsEntry.Identifier</c> or
    /// <c>ExtractedBook.Identifier</c>), when known.</summary>
    public string? Isbn { get; init; }

    /// <summary>The book's title, carried onto every chunk.</summary>
    public string? Title { get; init; }

    /// <summary>The book's authors, carried onto every chunk.</summary>
    public IReadOnlyList<string> Authors { get; init; } = Array.Empty<string>();

    /// <summary>A stable citation anchor for this chunk: the chapter's
    /// <c>ExtractedChapter.Href</c> (the spine item path inside the EPUB) —
    /// stable regardless of chunking parameters, usable to re-locate the
    /// source content.</summary>
    public required string SourceAnchor { get; init; }
}
