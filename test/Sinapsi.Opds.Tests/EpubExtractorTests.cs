using Sinapsi.Opds;
using Sinapsi.Opds.Models;
using Xunit;

namespace Sinapsi.Opds.Tests;

/// <summary>
/// EpubExtractor tests against SYNTHETIC EPUBs built in-memory (no committed
/// licensed content): EPUB3 (nav.xhtml TOC) + EPUB2 (toc.ncx TOC). Covers spine
/// order, chapter titles from the TOC, heading-hierarchy section splitting, clean
/// text (scripts/styles stripped, entities decoded, void-tag tolerance), and OPF
/// metadata. A final theory validates against a REAL EPUB on disk, skip-if-absent.
/// </summary>
public class EpubExtractorTests
{
    [Fact]
    public void Epub3_extracts_metadata_spine_order_and_toc_titles()
    {
        var book = EpubExtractor.Extract(SyntheticEpub.BuildEpub3());

        Assert.Equal("Synthetic EPUB3 Book", book.Title);
        Assert.Equal(new[] { "Test Author" }, book.Authors);
        Assert.Equal("en", book.Language);
        Assert.Equal("urn:isbn:9990000000099", book.Identifier);

        Assert.Equal(2, book.Chapters.Count);
        Assert.Equal(0, book.Chapters[0].Order);
        Assert.Equal(1, book.Chapters[1].Order);
        Assert.Equal("OEBPS/ch1.xhtml", book.Chapters[0].Href);
        // Chapter title comes from the nav TOC, not the doc heading/<title>.
        Assert.Equal("Chapter One (from nav)", book.Chapters[0].Heading);
        Assert.Equal("Chapter Two (from nav)", book.Chapters[1].Heading);
    }

    [Fact]
    public void Epub2_extracts_toc_titles_from_ncx()
    {
        var book = EpubExtractor.Extract(SyntheticEpub.BuildEpub2());

        Assert.Equal("Synthetic EPUB2 Book", book.Title);
        Assert.Equal(2, book.Chapters.Count);
        Assert.Equal("Chapter One (from ncx)", book.Chapters[0].Heading);
        Assert.Equal("Chapter Two (from ncx)", book.Chapters[1].Heading);
    }

    [Fact]
    public void Splits_sections_at_heading_boundaries_with_levels()
    {
        var book = EpubExtractor.Extract(SyntheticEpub.BuildEpub3());
        var ch1 = book.Chapters[0];

        // h1 "Introduction", h2 "Background", h2 "Motivation" -> 3 sections.
        Assert.Equal(3, ch1.Sections.Count);
        Assert.Equal("Introduction", ch1.Sections[0].Heading);
        Assert.Equal(1, ch1.Sections[0].HeadingLevel);
        Assert.Equal("Background", ch1.Sections[1].Heading);
        Assert.Equal(2, ch1.Sections[1].HeadingLevel);
        Assert.Equal("Motivation", ch1.Sections[2].Heading);
        Assert.Equal(2, ch1.Sections[2].HeadingLevel);
    }

    [Fact]
    public void Cleans_text_strips_scripts_styles_and_decodes_entities()
    {
        var book = EpubExtractor.Extract(SyntheticEpub.BuildEpub3());
        var ch1 = book.Chapters[0];
        var full = ch1.Text;

        // &nbsp; decoded to a space; &amp; decoded to '&'.
        Assert.Contains("This is the first paragraph with a non-breaking space.", full);
        Assert.Contains("Second paragraph & an ampersand.", full);
        // <style> and <script> content never leak into the text.
        Assert.DoesNotContain("font-size", full);
        Assert.DoesNotContain("color: red", full);
        Assert.DoesNotContain("console.log", full);
        Assert.DoesNotContain("should not appear", full);
        // The intro section holds both intro paragraphs (blank-line separated).
        Assert.Contains("\n\n", ch1.Sections[0].Text);
    }

    [Fact]
    public void Tolerates_epub2_unclosed_void_tags()
    {
        // Chapter 2 has "<p>A line<br>with a break.</p>" - XDocument would throw;
        // HtmlAgilityPack tolerates it and we still get the paragraph text.
        var book = EpubExtractor.Extract(SyntheticEpub.BuildEpub2());
        var ch2 = book.Chapters[1];
        Assert.Contains("A line", ch2.Text);
        Assert.Contains("with a break", ch2.Text);
        Assert.Contains("Another paragraph.", ch2.Text);
    }

    [Fact]
    public void ExtractDocument_is_directly_usable_without_a_zip()
    {
        var (title, sections) = EpubExtractor.ExtractDocument(
            "<html><head><title>T</title></head><body><h1>H</h1><p>Body&nbsp;text.</p></body></html>");
        Assert.Equal("T", title);
        var s = Assert.Single(sections);
        Assert.Equal("H", s.Heading);
        Assert.Equal("Body text.", s.Text);
    }

    [Fact]
    public void Missing_container_throws_format_exception()
    {
        // A zip with no META-INF/container.xml is not a valid EPUB.
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
            zip.CreateEntry("random.txt");
        ms.Position = 0;
        Assert.Throws<FormatException>(() => EpubExtractor.Extract(ms.ToArray()));
    }

    // --- Real-EPUB validation, skip-if-absent (NO licensed content committed) ---
    // Point SINAPSI_OPDS_REAL_EPUB at a real EPUB on disk (e.g. one scp'd from
    // CT147-booklore) to validate the extractor against a real O'Reilly EPUB at
    // test-run time. Absent env / file => the test no-ops (passes), never fails
    // CI, and the licensed file is never committed. Written as a self-skipping
    // [Fact] (xUnit 2.x has no dynamic Assert.Skip; an empty [Theory] data set
    // errors as "No data found").
    [Fact]
    public void Extracts_a_real_epub_when_present()
    {
        var path = Environment.GetEnvironmentVariable("SINAPSI_OPDS_REAL_EPUB");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return; // skip-if-absent: no real EPUB configured for this run.

        var book = EpubExtractor.Extract(File.ReadAllBytes(path));

        Assert.NotNull(book.Title);
        Assert.NotEmpty(book.Chapters);
        // Spine order is 0..n-1 monotonic.
        for (int i = 0; i < book.Chapters.Count; i++)
            Assert.Equal(i, book.Chapters[i].Order);
        // At least one chapter yields real prose text.
        Assert.Contains(book.Chapters, c => c.Text.Length > 200);
        // Headings were captured somewhere.
        Assert.Contains(book.Chapters, c => c.Heading is { Length: > 0 });
    }
}
