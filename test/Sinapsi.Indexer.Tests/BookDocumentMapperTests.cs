// ---------------------------------------------------------------------------
// BookDocumentMapperTests - unit proof of the M3 BookChunk -> Document mapping
// that M4's OpdsSourceScanner will call. Verifies field mapping, the JSON
// metadata payload shape, Kind/DocId/ContentSha derivation, and that the
// existing (non-book) Document shape is unaffected (Metadata defaults null).
// ---------------------------------------------------------------------------

using System.Text.Json;
using Sinapsi.Opds.Models;
using Xunit;

namespace Sinapsi.Indexer.Tests;

public class BookDocumentMapperTests
{
    private static BookChunk MakeChunk(
        string key = "book:isbn-1:0:0",
        string text = "Some chunk text with a few words in it.",
        int chapterOrder = 0,
        string? chapterHeading = "Chapter One",
        string? sectionHeading = "Intro",
        int chunkIndex = 0,
        IReadOnlyList<string>? categories = null,
        string? isbn = "isbn-1",
        string? title = "Domain-Driven Design",
        IReadOnlyList<string>? authors = null,
        string sourceAnchor = "ch0.xhtml") => new()
        {
            Key = key,
            Text = text,
            ChapterOrder = chapterOrder,
            ChapterHeading = chapterHeading,
            SectionHeading = sectionHeading,
            ChunkIndex = chunkIndex,
            Categories = categories ?? new[] { "Software Engineering", "Architecture" },
            Isbn = isbn,
            Title = title,
            Authors = authors ?? new[] { "Eric Evans" },
            SourceAnchor = sourceAnchor,
        };

    [Fact]
    public void ToDocument_MapsKeyFieldsDirectly()
    {
        var chunk = MakeChunk();

        var doc = BookDocumentMapper.ToDocument(chunk, source: "books");

        Assert.Equal(chunk.Key, doc.DocId);
        Assert.Equal("books", doc.Source);
        Assert.Equal(chunk.SourceAnchor, doc.Path);
        Assert.Equal("book_chunk", doc.Kind);
        Assert.Equal(chunk.Text, doc.Body);
    }

    [Fact]
    public void ToDocument_Kind_IsAlwaysBookChunk()
    {
        var doc = BookDocumentMapper.ToDocument(MakeChunk(), source: "books");
        Assert.Equal(BookDocumentMapper.BookChunkKind, doc.Kind);
        Assert.Equal("book_chunk", BookDocumentMapper.BookChunkKind);
    }

    [Fact]
    public void ToDocument_Title_CombinesBookTitleAndChapterHeading()
    {
        var doc = BookDocumentMapper.ToDocument(
            MakeChunk(title: "Domain-Driven Design", chapterHeading: "Chapter One"),
            source: "books");

        Assert.Equal("Domain-Driven Design - Chapter One", doc.Title);
    }

    [Fact]
    public void ToDocument_Title_FallsBackWhenChapterHeadingMissing()
    {
        var doc = BookDocumentMapper.ToDocument(
            MakeChunk(title: "Domain-Driven Design", chapterHeading: null),
            source: "books");

        Assert.Equal("Domain-Driven Design", doc.Title);
    }

    [Fact]
    public void ToDocument_Title_FallsBackWhenBookTitleMissing()
    {
        var doc = BookDocumentMapper.ToDocument(
            MakeChunk(title: null, chapterHeading: "Chapter One"),
            source: "books");

        Assert.Equal("Chapter One", doc.Title);
    }

    [Fact]
    public void ToDocument_Title_FallsBackToKey_WhenBothMissing()
    {
        var doc = BookDocumentMapper.ToDocument(
            MakeChunk(key: "book:x:0:0", title: null, chapterHeading: null),
            source: "books");

        Assert.Equal("book:x:0:0", doc.Title);
    }

    [Fact]
    public void ToDocument_ContentSha_IsSha256OfBodyText()
    {
        var chunk = MakeChunk(text: "distinctive chunk text");
        var doc = BookDocumentMapper.ToDocument(chunk, source: "books");

        Assert.Equal(GitSourceScanner.Sha256("distinctive chunk text"), doc.ContentSha);
    }

    [Fact]
    public void ToDocument_Scope_DefaultsEmpty_OrUsesSuppliedValue()
    {
        var chunkDoc = BookDocumentMapper.ToDocument(MakeChunk(), source: "books");
        Assert.Equal("", chunkDoc.Scope);

        var scopedDoc = BookDocumentMapper.ToDocument(MakeChunk(), source: "books", scope: "data-engineering");
        Assert.Equal("data-engineering", scopedDoc.Scope);
    }

    [Fact]
    public void ToDocument_Metadata_IsValidJson_WithExpectedFacets()
    {
        var chunk = MakeChunk(
            isbn: "978-0-13-468599-1",
            authors: new[] { "Eric Evans" },
            categories: new[] { "Software Engineering", "Architecture" },
            chapterOrder: 4,
            sectionHeading: "Bounded Contexts",
            sourceAnchor: "ch4.xhtml");

        var doc = BookDocumentMapper.ToDocument(chunk, source: "books");

        Assert.NotNull(doc.Metadata);
        using var parsed = JsonDocument.Parse(doc.Metadata!);
        var root = parsed.RootElement;

        Assert.Equal("978-0-13-468599-1", root.GetProperty("isbn").GetString());
        Assert.Equal("Eric Evans", root.GetProperty("authors")[0].GetString());
        Assert.Equal("Software Engineering", root.GetProperty("categories")[0].GetString());
        Assert.Equal(4, root.GetProperty("chapter").GetInt32());
        Assert.Equal("Bounded Contexts", root.GetProperty("heading").GetString());
        Assert.Equal("ch4.xhtml", root.GetProperty("source_anchor").GetString());
    }

    [Fact]
    public void ToDocument_Metadata_Heading_FallsBackToChapterHeading_WhenSectionHeadingNull()
    {
        var chunk = MakeChunk(chapterHeading: "Chapter One", sectionHeading: null);
        var doc = BookDocumentMapper.ToDocument(chunk, source: "books");

        using var parsed = JsonDocument.Parse(doc.Metadata!);
        Assert.Equal("Chapter One", parsed.RootElement.GetProperty("heading").GetString());
    }

    [Fact]
    public void ToDocument_Metadata_NullIsbn_SerialisesAsJsonNull()
    {
        var chunk = MakeChunk(isbn: null);
        var doc = BookDocumentMapper.ToDocument(chunk, source: "books");

        using var parsed = JsonDocument.Parse(doc.Metadata!);
        Assert.Equal(JsonValueKind.Null, parsed.RootElement.GetProperty("isbn").ValueKind);
    }

    [Fact]
    public void ToDocument_ThrowsOnEmptySource()
    {
        Assert.Throws<ArgumentException>(() => BookDocumentMapper.ToDocument(MakeChunk(), source: ""));
    }

    [Fact]
    public void ToDocument_ThrowsOnNullChunk()
    {
        Assert.Throws<ArgumentNullException>(() => BookDocumentMapper.ToDocument(null!, source: "books"));
    }

    // --- backward-compatibility proof: non-book Documents are unaffected -------

    [Fact]
    public void NonBookDocument_MetadataDefaultsToNull_ExistingConstructionUnaffected()
    {
        // This is the exact construction pattern used by GitSourceScanner / the
        // existing tests (SearchStoreDenylistTests etc.) for git-source docs —
        // proving the new Metadata property does not force any change there.
        var doc = new Document
        {
            DocId = "docs:overview.md",
            Source = "docs",
            Path = "overview.md",
            Kind = "doc",
            Title = "Overview",
            Body = "Some markdown body.",
            ContentSha = GitSourceScanner.Sha256("Some markdown body."),
        };

        Assert.Null(doc.Metadata);
    }
}
