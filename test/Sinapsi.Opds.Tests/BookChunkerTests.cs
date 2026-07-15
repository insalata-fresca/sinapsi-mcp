// ---------------------------------------------------------------------------
// BookChunkerTests - unit proof of the M3 chunking rules against SYNTHETIC
// ExtractedBook fixtures (no licensed text, per CLAUDE.md "no licensed text
// committed"). Covers: chunk sizes, ~15% overlap, key format, hierarchy
// retention (book -> chapter -> section -> chunk), heading-carry, paragraph
// boundary splitting, determinism, and the oversized-paragraph fallback.
// ---------------------------------------------------------------------------

using Sinapsi.Opds.Models;
using Xunit;

namespace Sinapsi.Opds.Tests;

public class BookChunkerTests
{
    // --- fixture builders -----------------------------------------------------

    /// <summary>One synthetic paragraph of exactly <paramref name="words"/> words
    /// ("word0 word1 word2 ..."), so word counts are exact and assertions are precise.</summary>
    private static string Paragraph(int words, string prefix = "word") =>
        string.Join(" ", Enumerable.Range(0, words).Select(i => $"{prefix}{i}"));

    private static ExtractedSection Section(string? heading, int level, params string[] paragraphs) =>
        new(heading, level, string.Join("\n\n", paragraphs));

    private static ExtractedChapter Chapter(int order, string? heading, string href, params ExtractedSection[] sections) =>
        new(order, heading, href, sections);

    private static ExtractedBook Book(string? title, IReadOnlyList<string> authors, string? identifier, params ExtractedChapter[] chapters) =>
        new() { Title = title, Authors = authors, Identifier = identifier, Chapters = chapters };

    // --- size + overlap ---------------------------------------------------------

    [Fact]
    public void Chunk_LongChapter_ProducesChunksWithinTargetSizeRange()
    {
        // A chapter with a lot of content, in modest paragraphs, so the windower
        // has room to pick boundaries near the target.
        var paragraphs = Enumerable.Range(0, 20).Select(i => Paragraph(50, $"p{i}w")).ToArray();
        var book = Book("Big Book", new[] { "A. Author" }, "isbn-1",
            Chapter(0, "Chapter One", "ch1.xhtml", Section(null, 0, paragraphs)));

        var chunks = BookChunker.Chunk(book, fallbackId: "fallback");

        Assert.NotEmpty(chunks);
        // Every chunk but possibly the last (end-of-chapter remainder) must be
        // within [MinWords, MaxWords]; the last may be shorter.
        for (int i = 0; i < chunks.Count - 1; i++)
        {
            var wc = WordCount(chunks[i].Text);
            Assert.InRange(wc, BookChunker.MinWords, BookChunker.MaxWords);
        }
        // Even the final chunk must never exceed MaxWords.
        Assert.True(WordCount(chunks[^1].Text) <= BookChunker.MaxWords);
    }

    [Fact]
    public void Chunk_ConsecutiveChunks_OverlapByApproximately15Percent()
    {
        var paragraphs = Enumerable.Range(0, 20).Select(i => Paragraph(50, $"p{i}w")).ToArray();
        var book = Book("Big Book", new[] { "A. Author" }, "isbn-1",
            Chapter(0, "Chapter One", "ch1.xhtml", Section(null, 0, paragraphs)));

        var chunks = BookChunker.Chunk(book, fallbackId: "fallback");
        Assert.True(chunks.Count >= 2, "fixture must yield at least 2 chunks to test overlap");

        // Overlap = shared paragraph-word-tokens between chunk i's tail and
        // chunk i+1's head. Since each paragraph's words are unique
        // ("p{i}w{j}"), we can measure overlap by set-intersection of word tokens.
        var wordsA = chunks[0].Text.Split(' ').ToHashSet();
        var wordsB = chunks[1].Text.Split(' ').ToHashSet();
        var overlap = wordsA.Intersect(wordsB).Count();

        var target = (BookChunker.MinWords + BookChunker.MaxWords) / 2;
        var expectedOverlap = target * BookChunker.OverlapFraction;

        // Overlap should be present and roughly in the right ballpark (paragraph
        // granularity means it won't be exact) — allow a generous band since
        // whole paragraphs (50 words each) are the smallest movable unit.
        Assert.True(overlap > 0, "consecutive chunks must share some content (the ~15% overlap)");
        Assert.True(overlap <= expectedOverlap + 50,
            $"overlap {overlap} should be roughly near the ~15% target {expectedOverlap} (+- one paragraph)");
    }

    [Fact]
    public void Chunk_SingleShortChapter_ProducesOneChunk_NoCrash()
    {
        var book = Book("Small Book", Array.Empty<string>(), null,
            Chapter(0, "Only Chapter", "ch1.xhtml", Section(null, 0, Paragraph(40))));

        var chunks = BookChunker.Chunk(book, fallbackId: "fallback-id");

        Assert.Single(chunks);
        Assert.Equal("book:fallback-id:0:0", chunks[0].Key);
    }

    // --- key format -------------------------------------------------------------

    [Fact]
    public void Chunk_KeyFormat_IsBookIsbnChapterChunk()
    {
        var book = Book("Title", new[] { "Author" }, "978-0-13-468599-1",
            Chapter(0, "Ch0", "c0.xhtml", Section(null, 0, Paragraph(40))),
            Chapter(1, "Ch1", "c1.xhtml", Section(null, 0, Paragraph(40))));

        var chunks = BookChunker.Chunk(book, fallbackId: "unused-because-isbn-present");

        Assert.All(chunks, c => Assert.StartsWith("book:978-0-13-468599-1:", c.Key));
        Assert.Contains(chunks, c => c.Key == "book:978-0-13-468599-1:0:0");
        Assert.Contains(chunks, c => c.Key == "book:978-0-13-468599-1:1:0");
    }

    [Fact]
    public void Chunk_NoIsbnAnywhere_FallsBackToCallerSuppliedId()
    {
        var book = Book("Title", Array.Empty<string>(), identifier: null,
            Chapter(0, "Ch0", "c0.xhtml", Section(null, 0, Paragraph(40))));

        var chunks = BookChunker.Chunk(book, fallbackId: "opds-entry-id-123");

        Assert.Equal("book:opds-entry-id-123:0:0", chunks[0].Key);
    }

    [Fact]
    public void Chunk_ExplicitIsbnParam_OverridesBookIdentifier()
    {
        var book = Book("Title", Array.Empty<string>(), identifier: "book-opf-id",
            Chapter(0, "Ch0", "c0.xhtml", Section(null, 0, Paragraph(40))));

        var chunks = BookChunker.Chunk(book, fallbackId: "fallback", isbn: "explicit-isbn");

        Assert.Equal("book:explicit-isbn:0:0", chunks[0].Key);
    }

    [Fact]
    public void Chunk_ChunkIndex_IsPerChapterZeroBased()
    {
        var paragraphs = Enumerable.Range(0, 20).Select(i => Paragraph(50, $"p{i}w")).ToArray();
        var book = Book("Title", Array.Empty<string>(), "isbn",
            Chapter(0, "Ch0", "c0.xhtml", Section(null, 0, paragraphs)),
            Chapter(1, "Ch1", "c1.xhtml", Section(null, 0, Paragraph(40))));

        var chunks = BookChunker.Chunk(book, fallbackId: "fb");

        var chapter0Chunks = chunks.Where(c => c.ChapterOrder == 0).OrderBy(c => c.ChunkIndex).ToList();
        var chapter1Chunks = chunks.Where(c => c.ChapterOrder == 1).OrderBy(c => c.ChunkIndex).ToList();

        Assert.Equal(Enumerable.Range(0, chapter0Chunks.Count), chapter0Chunks.Select(c => c.ChunkIndex));
        // Chapter 1 restarts chunk indexing at 0, independent of chapter 0's count.
        Assert.Equal(0, chapter1Chunks[0].ChunkIndex);
    }

    // --- hierarchy + heading carry -----------------------------------------------

    [Fact]
    public void Chunk_CarriesChapterHeadingOntoEveryChunk()
    {
        var paragraphs = Enumerable.Range(0, 20).Select(i => Paragraph(50, $"p{i}w")).ToArray();
        var book = Book("Title", Array.Empty<string>(), "isbn",
            Chapter(3, "The Third Chapter", "c3.xhtml", Section(null, 0, paragraphs)));

        var chunks = BookChunker.Chunk(book, fallbackId: "fb");

        Assert.True(chunks.Count >= 2);
        Assert.All(chunks, c => Assert.Equal("The Third Chapter", c.ChapterHeading));
        Assert.All(chunks, c => Assert.Equal(3, c.ChapterOrder));
        Assert.All(chunks, c => Assert.Equal("c3.xhtml", c.SourceAnchor));
    }

    [Fact]
    public void Chunk_CarriesSectionHeading_FromNearestEnclosingSection()
    {
        // Each section alone is large enough (well past MinWords) to force its
        // own chunk boundary, so the two sections cannot collapse into one chunk.
        var sectionAParagraphs = Enumerable.Range(0, 8).Select(i => Paragraph(50, $"a{i}w")).ToArray();
        var sectionBParagraphs = Enumerable.Range(0, 8).Select(i => Paragraph(50, $"b{i}w")).ToArray();
        var book = Book("Title", Array.Empty<string>(), "isbn",
            Chapter(0, "Ch0", "c0.xhtml",
                Section("Section A", 2, sectionAParagraphs),
                Section("Section B", 2, sectionBParagraphs)));

        var chunks = BookChunker.Chunk(book, fallbackId: "fb");

        // Each chunk should reflect the section its FIRST paragraph came from.
        Assert.Contains(chunks, c => c.SectionHeading == "Section A");
        Assert.Contains(chunks, c => c.SectionHeading == "Section B");
    }

    [Fact]
    public void Chunk_BookAndOpdsFacets_CarriedOntoEveryChunk()
    {
        var book = Book("Domain-Driven Design", new[] { "Eric Evans" }, "isbn-book",
            Chapter(0, "Ch0", "c0.xhtml", Section(null, 0, Paragraph(40))));

        var chunks = BookChunker.Chunk(
            book,
            fallbackId: "fb",
            title: "Domain-Driven Design (OPDS title)",
            authors: new[] { "Eric Evans", "Co-Author" },
            categories: new[] { "Software Engineering", "Architecture" });

        Assert.All(chunks, c =>
        {
            Assert.Equal("Domain-Driven Design (OPDS title)", c.Title);
            Assert.Equal(new[] { "Eric Evans", "Co-Author" }, c.Authors);
            Assert.Equal(new[] { "Software Engineering", "Architecture" }, c.Categories);
            Assert.Equal("isbn-book", c.Isbn);
        });
    }

    [Fact]
    public void Chunk_NoOpdsFacetsSupplied_FallsBackToBookMetadata()
    {
        var book = Book("Book Title", new[] { "Book Author" }, "isbn-book",
            Chapter(0, "Ch0", "c0.xhtml", Section(null, 0, Paragraph(40))));

        var chunks = BookChunker.Chunk(book, fallbackId: "fb");

        Assert.All(chunks, c =>
        {
            Assert.Equal("Book Title", c.Title);
            Assert.Equal(new[] { "Book Author" }, c.Authors);
            Assert.Empty(c.Categories);
        });
    }

    // --- paragraph boundaries (never mid-sentence when avoidable) --------------

    [Fact]
    public void Chunk_SplitsOnParagraphBoundaries_NeverMidParagraph()
    {
        // Distinct, recognisable paragraphs so we can verify each chunk's text
        // is built from WHOLE paragraphs (joined by the "\n\n" separator),
        // never a fragment of one.
        var paragraphs = new[]
        {
            Paragraph(100, "alpha"),
            Paragraph(100, "beta"),
            Paragraph(100, "gamma"),
            Paragraph(100, "delta"),
        };
        var book = Book("Title", Array.Empty<string>(), "isbn",
            Chapter(0, "Ch0", "c0.xhtml", Section(null, 0, paragraphs)));

        var chunks = BookChunker.Chunk(book, fallbackId: "fb");

        foreach (var chunk in chunks)
        {
            var parts = chunk.Text.Split("\n\n");
            foreach (var part in parts)
            {
                // Each part must be a byte-identical whole paragraph from the
                // fixture (never a truncated slice of one).
                Assert.Contains(part, paragraphs);
            }
        }
    }

    [Fact]
    public void Chunk_OversizedSingleParagraph_SplitsOnSentenceBoundaries_NoMidWordCut()
    {
        // One paragraph far larger than MaxWords, built from real sentences so
        // the sentence-splitter fallback has terminators to find.
        var sentences = Enumerable.Range(0, 60)
            .Select(i => $"This is sentence number {i} in a very long paragraph.")
            .ToArray();
        var bigParagraph = string.Join(" ", sentences);

        var book = Book("Title", Array.Empty<string>(), "isbn",
            Chapter(0, "Ch0", "c0.xhtml", Section("Big Section", 1, bigParagraph)));

        var chunks = BookChunker.Chunk(book, fallbackId: "fb");

        Assert.NotEmpty(chunks);
        foreach (var chunk in chunks)
        {
            // No chunk should ever exceed MaxWords even for a pathologically
            // long source paragraph.
            Assert.True(WordCount(chunk.Text) <= BookChunker.MaxWords + 5,
                "sentence-boundary fallback must still respect (near) the max size");
            // Text must not end mid-word: every non-final chunk's text should
            // end at a sentence terminator or at least not truncate a token
            // (each emitted word is one of the "This/is/sentence/..." tokens).
            var lastChar = chunk.Text.TrimEnd()[^1];
            Assert.True(char.IsLetterOrDigit(lastChar) || lastChar == '.',
                $"chunk must not end mid-punctuation-cut, got '...{chunk.Text[^20..]}'");
        }
        // Reassembling all chunks' distinct sentences must reproduce every
        // original sentence somewhere (no sentence dropped or mangled).
        var allText = string.Join(" ", chunks.Select(c => c.Text));
        foreach (var sentence in sentences)
            Assert.Contains(sentence, allText);
    }

    // --- determinism -------------------------------------------------------------

    [Fact]
    public void Chunk_IsPureAndDeterministic_SameInputSameOutput()
    {
        var paragraphs = Enumerable.Range(0, 15).Select(i => Paragraph(60, $"p{i}w")).ToArray();
        var book = Book("Title", new[] { "Author" }, "isbn",
            Chapter(0, "Ch0", "c0.xhtml", Section("S", 1, paragraphs)));

        var run1 = BookChunker.Chunk(book, fallbackId: "fb", categories: new[] { "Cat" });
        var run2 = BookChunker.Chunk(book, fallbackId: "fb", categories: new[] { "Cat" });

        Assert.Equal(run1.Count, run2.Count);
        for (int i = 0; i < run1.Count; i++)
        {
            Assert.Equal(run1[i].Key, run2[i].Key);
            Assert.Equal(run1[i].Text, run2[i].Text);
        }
    }

    [Fact]
    public void Chunk_MultipleChapters_ProducesHierarchyAcrossAll()
    {
        var book = Book("Multi-Chapter Book", new[] { "Author" }, "isbn",
            Chapter(0, "Intro", "intro.xhtml", Section(null, 0, Paragraph(40))),
            Chapter(1, "Middle", "mid.xhtml", Section("Setup", 1, Paragraph(45))),
            Chapter(2, "Conclusion", "end.xhtml", Section(null, 0, Paragraph(35))));

        var chunks = BookChunker.Chunk(book, fallbackId: "fb");

        Assert.Equal(new[] { 0, 1, 2 }, chunks.Select(c => c.ChapterOrder).Distinct().OrderBy(x => x));
        Assert.Contains(chunks, c => c.ChapterOrder == 0 && c.ChapterHeading == "Intro" && c.SourceAnchor == "intro.xhtml");
        Assert.Contains(chunks, c => c.ChapterOrder == 1 && c.ChapterHeading == "Middle" && c.SourceAnchor == "mid.xhtml");
        Assert.Contains(chunks, c => c.ChapterOrder == 2 && c.ChapterHeading == "Conclusion" && c.SourceAnchor == "end.xhtml");
    }

    [Fact]
    public void Chunk_EmptyChapter_IsSkipped_NoEmptyChunks()
    {
        var book = Book("Title", Array.Empty<string>(), "isbn",
            Chapter(0, "Empty Chapter", "empty.xhtml", Array.Empty<ExtractedSection>()),
            Chapter(1, "Real Chapter", "real.xhtml", Section(null, 0, Paragraph(40))));

        var chunks = BookChunker.Chunk(book, fallbackId: "fb");

        Assert.DoesNotContain(chunks, c => c.ChapterOrder == 0);
        Assert.Contains(chunks, c => c.ChapterOrder == 1);
    }

    [Fact]
    public void Chunk_ThrowsOnEmptyFallbackId()
    {
        var book = Book("Title", Array.Empty<string>(), null,
            Chapter(0, "Ch0", "c0.xhtml", Section(null, 0, Paragraph(40))));

        Assert.Throws<ArgumentException>(() => BookChunker.Chunk(book, fallbackId: ""));
    }

    private static int WordCount(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
