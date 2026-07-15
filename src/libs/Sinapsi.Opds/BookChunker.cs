// ---------------------------------------------------------------------------
// BookChunker - ExtractedBook -> ordered BookChunks for embedding + retrieval.
//
// Per design doc SS3.3: ~300-500 words per chunk, ~15% overlap, keyed
// "book:<isbn-or-id>:<chapterOrder>:<chunkIndex>", hierarchy retained
// (book -> chapter -> section -> chunk) so a hit can expand to its parent
// section/chapter (SS3.4 "small-to-big").
//
// Pure + deterministic: no I/O, no randomness, no wall-clock. Same ExtractedBook
// + same facets in -> byte-identical chunk sequence out. Splits on paragraph
// boundaries (ExtractedSection.Text is already blank-line-separated paragraphs)
// so a chunk boundary lands between paragraphs, never mid-sentence, EXCEPT for
// the rare case of a single paragraph longer than the max chunk size, where a
// sentence-boundary split is used as a fallback (never a hard mid-word cut).
//
// Plain-ASCII banner so this source diffs as TEXT, never binary.
// ---------------------------------------------------------------------------

using System.Text;
using Sinapsi.Opds.Models;

namespace Sinapsi.Opds;

/// <summary>Splits an <see cref="ExtractedBook"/> into <see cref="BookChunk"/>s. Stateless.</summary>
public static class BookChunker
{
    /// <summary>Target chunk size lower bound, in words.</summary>
    public const int MinWords = 300;

    /// <summary>Target chunk size upper bound, in words.</summary>
    public const int MaxWords = 500;

    /// <summary>Overlap between consecutive chunks, as a fraction of the target
    /// chunk size (~15%, per design doc SS3.3).</summary>
    public const double OverlapFraction = 0.15;

    /// <summary>
    /// Chunk a book into ordered <see cref="BookChunk"/>s, carrying the given
    /// book-level facets (from the source <c>OpdsEntry</c>) onto every chunk.
    /// </summary>
    /// <param name="book">The extracted book (chapters in spine order).</param>
    /// <param name="fallbackId">Used in the chunk key in place of an ISBN when
    /// neither <paramref name="isbn"/> nor <c>book.Identifier</c> is set (e.g. an
    /// OPDS entry id). Must be non-empty.</param>
    /// <param name="isbn">The book's ISBN/identifier; falls back to
    /// <c>book.Identifier</c>, then <paramref name="fallbackId"/>.</param>
    /// <param name="title">Overrides <c>book.Title</c> when supplied (e.g. the
    /// OPDS entry's title, which may be more complete/canonical than the OPF).</param>
    /// <param name="authors">Overrides <c>book.Authors</c> when non-empty.</param>
    /// <param name="categories">BookLore/OPDS category facets to carry onto every chunk.</param>
    public static IReadOnlyList<BookChunk> Chunk(
        ExtractedBook book,
        string fallbackId,
        string? isbn = null,
        string? title = null,
        IReadOnlyList<string>? authors = null,
        IReadOnlyList<string>? categories = null)
    {
        ArgumentNullException.ThrowIfNull(book);
        if (string.IsNullOrWhiteSpace(fallbackId))
            throw new ArgumentException("fallbackId must be non-empty.", nameof(fallbackId));

        var effectiveIsbn = FirstNonEmpty(isbn, book.Identifier);
        var keyId = FirstNonEmpty(effectiveIsbn, fallbackId)!;
        var effectiveTitle = FirstNonEmpty(title, book.Title);
        var effectiveAuthors = (authors is { Count: > 0 }) ? authors : book.Authors;
        var effectiveCategories = categories ?? Array.Empty<string>();

        var chunks = new List<BookChunk>();
        foreach (var chapter in book.Chapters)
        {
            var units = ExplodeToParagraphUnits(chapter);
            if (units.Count == 0) continue;

            var windows = WindowParagraphs(units);
            for (int i = 0; i < windows.Count; i++)
            {
                var w = windows[i];
                chunks.Add(new BookChunk
                {
                    Key = $"book:{keyId}:{chapter.Order}:{i}",
                    Text = string.Join("\n\n", w.Select(u => u.Text)),
                    ChapterOrder = chapter.Order,
                    ChapterHeading = chapter.Heading,
                    SectionHeading = w[0].SectionHeading,
                    ChunkIndex = i,
                    Categories = effectiveCategories,
                    Isbn = effectiveIsbn,
                    Title = effectiveTitle,
                    Authors = effectiveAuthors,
                    SourceAnchor = chapter.Href,
                });
            }
        }
        return chunks;
    }

    // --- paragraph-unit extraction -------------------------------------------

    /// <summary>One paragraph (or sentence-split fragment, for an oversized
    /// paragraph) tagged with the section heading it was drawn from.</summary>
    private readonly record struct ParagraphUnit(string Text, string? SectionHeading, int WordCount);

    /// <summary>
    /// Flatten a chapter's sections into an ordered list of paragraph units,
    /// splitting any single paragraph that alone exceeds <see cref="MaxWords"/>
    /// into sentence-boundary fragments (never a hard mid-word/mid-sentence cut).
    /// </summary>
    private static List<ParagraphUnit> ExplodeToParagraphUnits(ExtractedChapter chapter)
    {
        var units = new List<ParagraphUnit>();
        foreach (var section in chapter.Sections)
        {
            if (string.IsNullOrWhiteSpace(section.Text)) continue;
            foreach (var para in section.Text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = para.Trim();
                if (trimmed.Length == 0) continue;
                var wc = WordCount(trimmed);
                if (wc <= MaxWords)
                {
                    units.Add(new ParagraphUnit(trimmed, section.Heading, wc));
                }
                else
                {
                    // Oversized single paragraph: fall back to sentence-boundary
                    // splitting so a chunk boundary still never lands mid-sentence.
                    foreach (var sentGroup in GroupSentences(trimmed, MaxWords))
                        units.Add(new ParagraphUnit(sentGroup, section.Heading, WordCount(sentGroup)));
                }
            }
        }
        return units;
    }

    /// <summary>Group a long paragraph's sentences into fragments each at most
    /// <paramref name="maxWords"/> words (best-effort; a single sentence longer
    /// than the cap is kept whole rather than cut mid-word).</summary>
    private static IEnumerable<string> GroupSentences(string paragraph, int maxWords)
    {
        var sentences = SplitSentences(paragraph);
        var current = new StringBuilder();
        int currentWords = 0;
        foreach (var sentence in sentences)
        {
            var sw = WordCount(sentence);
            if (currentWords > 0 && currentWords + sw > maxWords)
            {
                yield return current.ToString().Trim();
                current.Clear();
                currentWords = 0;
            }
            if (current.Length > 0) current.Append(' ');
            current.Append(sentence);
            currentWords += sw;
        }
        if (current.Length > 0) yield return current.ToString().Trim();
    }

    /// <summary>Naive but dependency-free sentence splitter: breaks after
    /// ./!/? followed by whitespace + an uppercase/digit/quote, keeping the
    /// terminator attached to its sentence. Good enough for chunk-boundary
    /// purposes (never mid-word); not a linguistic sentence tokenizer.</summary>
    private static List<string> SplitSentences(string text)
    {
        var result = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '.' or '!' or '?')
            {
                // Consume any trailing closing quotes/parens after the terminator.
                int end = i + 1;
                while (end < text.Length && (text[end] is '"' or '\'' or ')' or '”')) end++;
                if (end >= text.Length || char.IsWhiteSpace(text[end]))
                {
                    result.Add(text[start..end].Trim());
                    start = end;
                }
            }
        }
        if (start < text.Length)
        {
            var tail = text[start..].Trim();
            if (tail.Length > 0) result.Add(tail);
        }
        return result.Count > 0 ? result : new List<string> { text };
    }

    // --- windowing ------------------------------------------------------------

    /// <summary>
    /// Slide a window of paragraph units into chunks of [MinWords, MaxWords]
    /// (best-effort — the last chunk of a chapter may be shorter), advancing by
    /// (1 - OverlapFraction) of EACH window's own size each step so consecutive
    /// chunks share ~15% of their content (the tail of chunk N reappears as the
    /// head of chunk N+1), enabling small-to-big continuity across chunk
    /// boundaries.
    /// </summary>
    private static List<List<ParagraphUnit>> WindowParagraphs(List<ParagraphUnit> units)
    {
        var windows = new List<List<ParagraphUnit>>();

        int idx = 0;
        while (idx < units.Count)
        {
            var window = new List<ParagraphUnit>();
            int words = 0;
            int j = idx;
            while (j < units.Count)
            {
                var u = units[j];
                // Stop BEFORE exceeding MaxWords, unless the window is still empty
                // (a single oversized unit must go in its own chunk rather than
                // never being emitted).
                if (words > 0 && words + u.WordCount > MaxWords) break;
                window.Add(u);
                words += u.WordCount;
                j++;
                if (words >= MinWords) break;
            }
            // If we stopped short of MinWords only because we ran out of units
            // (end of chapter), that's fine — it's the final, shorter chunk.
            windows.Add(window);

            if (j >= units.Count) break;

            // Advance start by (1 - overlap) of THIS window's actual word count
            // (not the fixed target — a window can end up shorter than target
            // when unit sizes don't divide evenly), so the next window overlaps
            // the tail of this one by ~OverlapFraction of the window size. Using
            // the window's own size (rather than the fixed target) guarantees
            // advanceWords < words, so the advance always stops strictly inside
            // the window, leaving a real overlapping tail.
            int advanceWords = (int)Math.Round(words * (1 - OverlapFraction));
            int consumed = 0;
            int nextIdx = idx;
            while (nextIdx < j - 1 && consumed + units[nextIdx].WordCount <= advanceWords)
            {
                consumed += units[nextIdx].WordCount;
                nextIdx++;
            }
            // Guarantee forward progress even if a single unit's word count
            // alone exceeds advanceWords (avoid an infinite loop).
            if (nextIdx <= idx) nextIdx = idx + 1;
            idx = nextIdx;
        }

        return windows;
    }

    private static int WordCount(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static string? FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
}
