using Sinapsi.Opds;
using Sinapsi.Opds.Models;
using Xunit;

namespace Sinapsi.Opds.Tests;

/// <summary>
/// Parser tests against AUTHORED, spec-conformant OPDS 1.2 Atom fixtures (no
/// licensed catalog text; synthetic entries). Covers navigation feeds,
/// acquisition feeds, pagination (rel="next"), relative-href resolution, and the
/// entry model (authors/categories/language/identifier/updated/epub-link pick).
/// </summary>
public class OpdsFeedParserTests
{
    // A navigation feed: entries link only to sub-catalogs (no acquisition links).
    private const string NavFeed = """
        <?xml version="1.0" encoding="UTF-8"?>
        <feed xmlns="http://www.w3.org/2005/Atom"
              xmlns:opds="http://opds-spec.org/2010/catalog">
          <id>urn:test:root</id>
          <title>Test Library</title>
          <link rel="self" href="/opds" type="application/atom+xml;profile=opds-catalog;kind=navigation"/>
          <link rel="start" href="/opds" type="application/atom+xml;profile=opds-catalog;kind=navigation"/>
          <entry>
            <id>urn:test:nav:all</id>
            <title>All Books</title>
            <updated>2026-01-01T00:00:00Z</updated>
            <link rel="subsection" href="/opds/books"
                  type="application/atom+xml;profile=opds-catalog;kind=acquisition"/>
          </entry>
          <entry>
            <id>urn:test:nav:cats</id>
            <title>Categories</title>
            <link rel="subsection" href="categories"
                  type="application/atom+xml;profile=opds-catalog;kind=navigation"/>
          </entry>
        </feed>
        """;

    // An acquisition feed page 1 with a rel="next" pagination link. One entry has
    // an EPUB acquisition link (+ a sample + a cover image link to prove filtering).
    private const string AcqFeedPage1 = """
        <?xml version="1.0" encoding="UTF-8"?>
        <feed xmlns="http://www.w3.org/2005/Atom"
              xmlns:dcterms="http://purl.org/dc/terms/"
              xmlns:opds="http://opds-spec.org/2010/catalog">
          <id>urn:test:books:p1</id>
          <title>All Books</title>
          <link rel="self" href="/opds/books?page=1" type="application/atom+xml;profile=opds-catalog"/>
          <link rel="next" href="/opds/books?page=2" type="application/atom+xml;profile=opds-catalog"/>
          <entry>
            <id>urn:isbn:9990000000001</id>
            <title>The First Synthetic Book</title>
            <author><name>Ada Testwriter</name></author>
            <author><name>Ben Fixture</name></author>
            <category term="Software Engineering"/>
            <category term="Testing" label="QA"/>
            <dcterms:language>en</dcterms:language>
            <dcterms:identifier>urn:isbn:9990000000001</dcterms:identifier>
            <updated>2026-02-01T10:00:00Z</updated>
            <summary>A short synthetic summary.</summary>
            <link rel="http://opds-spec.org/image/thumbnail" href="/covers/1.png" type="image/png"/>
            <link rel="http://opds-spec.org/acquisition/sample" href="/dl/1-sample.epub" type="application/epub+zip"/>
            <link rel="http://opds-spec.org/acquisition" href="/dl/1.epub" type="application/epub+zip"/>
            <link rel="http://opds-spec.org/acquisition" href="/dl/1.pdf" type="application/pdf"/>
          </entry>
        </feed>
        """;

    // Acquisition feed page 2: no rel="next" (last page).
    private const string AcqFeedPage2 = """
        <?xml version="1.0" encoding="UTF-8"?>
        <feed xmlns="http://www.w3.org/2005/Atom">
          <id>urn:test:books:p2</id>
          <title>All Books</title>
          <link rel="self" href="/opds/books?page=2" type="application/atom+xml"/>
          <entry>
            <id>urn:isbn:9990000000002</id>
            <title>The Second Synthetic Book</title>
            <author><name>Cara Author</name></author>
            <updated>2026-03-15T08:30:00Z</updated>
            <link rel="http://opds-spec.org/acquisition/open-access" href="/dl/2.epub" type="application/epub+zip"/>
          </entry>
        </feed>
        """;

    private static readonly Uri BaseUri = new("http://library.test:6060/api/v1/opds");

    [Fact]
    public void Parses_navigation_feed_metadata_and_marks_it_navigation()
    {
        var feed = OpdsFeedParser.Parse(NavFeed, BaseUri);

        Assert.Equal("urn:test:root", feed.Id);
        Assert.Equal("Test Library", feed.Title);
        Assert.Equal(2, feed.Entries.Count);
        Assert.True(feed.IsNavigation);
        Assert.Null(feed.NextHref);
        // Entries in a nav feed carry no acquisition links.
        Assert.All(feed.Entries, e => Assert.Empty(e.AcquisitionLinks));
    }

    [Fact]
    public void Parses_acquisition_entry_full_model()
    {
        var feed = OpdsFeedParser.Parse(AcqFeedPage1, BaseUri);
        var e = Assert.Single(feed.Entries);

        Assert.Equal("urn:isbn:9990000000001", e.Id);
        Assert.Equal("The First Synthetic Book", e.Title);
        Assert.Equal(new[] { "Ada Testwriter", "Ben Fixture" }, e.Authors);
        Assert.Equal(new[] { "Software Engineering", "Testing" }, e.Categories);
        Assert.Equal("en", e.Language);
        Assert.Equal("urn:isbn:9990000000001", e.Identifier);
        Assert.Equal("A short synthetic summary.", e.Summary);
        Assert.NotNull(e.Updated);
        Assert.Equal(new DateTimeOffset(2026, 2, 1, 10, 0, 0, TimeSpan.Zero), e.Updated!.Value);
    }

    [Fact]
    public void Acquisition_links_exclude_cover_images_and_epub_link_prefers_plain_acquisition()
    {
        var feed = OpdsFeedParser.Parse(AcqFeedPage1, BaseUri);
        var e = Assert.Single(feed.Entries);

        // Cover thumbnail (image/*) is NOT an acquisition link; sample+epub+pdf are.
        Assert.Equal(3, e.AcquisitionLinks.Count);
        Assert.DoesNotContain(e.AcquisitionLinks, l => l.Type == "image/png");

        // EpubLink prefers the plain .../acquisition EPUB over the sample EPUB, and
        // never returns the PDF.
        Assert.NotNull(e.EpubLink);
        Assert.Equal("application/epub+zip", e.EpubLink!.Type);
        Assert.Equal("http://library.test:6060/dl/1.epub", e.EpubLink.Href);
        Assert.Equal("http://opds-spec.org/acquisition", e.EpubLink.Rel);
    }

    [Fact]
    public void Resolves_relative_next_href_against_request_uri()
    {
        var feed = OpdsFeedParser.Parse(AcqFeedPage1, BaseUri);
        Assert.Equal("http://library.test:6060/opds/books?page=2", feed.NextHref);
    }

    [Fact]
    public void Leaves_hrefs_relative_when_no_base_uri()
    {
        var feed = OpdsFeedParser.Parse(AcqFeedPage1, requestUri: null);
        Assert.Equal("/opds/books?page=2", feed.NextHref);
        var e = Assert.Single(feed.Entries);
        Assert.Equal("/dl/1.epub", e.EpubLink!.Href);
    }

    [Fact]
    public void Last_page_has_no_next()
    {
        var feed = OpdsFeedParser.Parse(AcqFeedPage2, BaseUri);
        Assert.Null(feed.NextHref);
        var e = Assert.Single(feed.Entries);
        Assert.Equal("http://opds-spec.org/acquisition/open-access", e.EpubLink!.Rel);
    }

    [Fact]
    public void Non_feed_root_throws()
    {
        Assert.Throws<FormatException>(() =>
            OpdsFeedParser.Parse("<html xmlns=\"http://www.w3.org/2005/Atom\"><body/></html>"));
    }

    // Expose the multi-page fixtures for the client pagination test.
    internal static string NavFeedXml => NavFeed;
    internal static string AcqPage1Xml => AcqFeedPage1;
    internal static string AcqPage2Xml => AcqFeedPage2;
}
