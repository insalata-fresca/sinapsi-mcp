// ---------------------------------------------------------------------------
// BookDocumentMapper - Sinapsi.Opds.BookChunk -> Sinapsi.Indexer.Document.
//
// M4's OpdsSourceScanner will call ToDocument() to turn each BookChunker output
// into the ONE flat write-model row the indexer already persists everything
// through (PostgresIndexStore.UpsertAsync). This mapper is the sole seam
// between the book-pipeline chunk shape and the indexer's Document shape —
// M4 does not need to know Document's field layout, just call ToDocument.
//
// Plain-ASCII banner so this source diffs as TEXT, never binary.
// ---------------------------------------------------------------------------

using System.Text.Json;
using System.Text.Json.Serialization;
using Sinapsi.Opds.Models;

namespace Sinapsi.Indexer;

/// <summary>Maps a <see cref="BookChunk"/> to the indexer's <see cref="Document"/>
/// write model, including its JSON <see cref="Document.Metadata"/> facet payload.</summary>
public static class BookDocumentMapper
{
    /// <summary>The <see cref="Document.Kind"/> value for every book-chunk document.</summary>
    public const string BookChunkKind = "book_chunk";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Deterministic, compact — this is a storage payload, not a wire API.
        WriteIndented = false,
    };

    /// <summary>
    /// Map one <see cref="BookChunk"/> to a <see cref="Document"/> ready for
    /// <c>IIndexStore.UpsertAsync</c>. <paramref name="source"/> is the logical
    /// books source name (the caller's <c>ISourceRef.Source</c>, e.g. "books").
    /// <see cref="Document.DocId"/> is <c>chunk.Key</c> directly — already
    /// globally unique and stable per the BookChunker key format.
    /// </summary>
    public static Document ToDocument(BookChunk chunk, string source, string scope = "")
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("source must be non-empty.", nameof(source));

        var metadata = new BookChunkMetadata(
            Isbn: chunk.Isbn,
            Authors: chunk.Authors,
            Categories: chunk.Categories,
            Chapter: chunk.ChapterOrder,
            Heading: chunk.SectionHeading ?? chunk.ChapterHeading,
            Anchor: chunk.SourceAnchor);

        return new Document
        {
            DocId = chunk.Key,
            Source = source,
            Path = chunk.SourceAnchor,
            Kind = BookChunkKind,
            Title = BuildTitle(chunk),
            Body = chunk.Text,
            ContentSha = GitSourceScanner.Sha256(chunk.Text),
            Scope = scope,
            Metadata = JsonSerializer.Serialize(metadata, JsonOptions),
        };
    }

    /// <summary>Human-readable title: "{book title} - {chapter heading}" (falls
    /// back gracefully when either half is missing).</summary>
    private static string BuildTitle(BookChunk chunk)
    {
        var bookTitle = string.IsNullOrWhiteSpace(chunk.Title) ? null : chunk.Title;
        var chapterHeading = string.IsNullOrWhiteSpace(chunk.ChapterHeading) ? null : chunk.ChapterHeading;
        return (bookTitle, chapterHeading) switch
        {
            (not null, not null) => $"{bookTitle} - {chapterHeading}",
            (not null, null) => bookTitle,
            (null, not null) => chapterHeading,
            (null, null) => chunk.Key,
        };
    }

    /// <summary>The JSON shape stored in <see cref="Document.Metadata"/> for a
    /// book-chunk document — the facets the design doc (SS3.3/SS3.5) calls for:
    /// isbn/title/authors/categories/chapter/heading/source_anchor. (Title is
    /// carried in Document.Title itself, not duplicated here.) Property names are
    /// pinned via <see cref="JsonPropertyNameAttribute"/> to the exact lower-
    /// snake_case facet names from the design doc, independent of C# casing.</summary>
    public sealed record BookChunkMetadata(
        [property: JsonPropertyName("isbn")] string? Isbn,
        [property: JsonPropertyName("authors")] IReadOnlyList<string> Authors,
        [property: JsonPropertyName("categories")] IReadOnlyList<string> Categories,
        [property: JsonPropertyName("chapter")] int Chapter,
        [property: JsonPropertyName("heading")] string? Heading,
        [property: JsonPropertyName("source_anchor")] string Anchor);
}
