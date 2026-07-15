// ---------------------------------------------------------------------------
// OpdsNavTraversalTests - M5b: OpdsClient must TRAVERSE OPDS navigation feeds to
// reach the acquisition (book) feeds. A real OPDS root (BookLore, Calibre-Web,
// Kavita, Komga) is a NAVIGATION feed: its entries are catalog links (All Books,
// Authors, Series, ...) with NO acquisition links. The M4 scanner calls
// EnumerateAllAsync(root) - so that method must descend nav sub-feeds and return
// the full, de-duplicated set of acquisition entries, or the books tenant indexes
// 0 books (the live bug this mission fixes).
//
// All fixtures are AUTHORED + synthetic (no licensed catalog text); all HTTP is a
// URL-keyed fake handler (no network). The handler records every fetched feed URL
// + Authorization header so tests can assert auth-on-every-subfetch, cycle-guard
// (no revisits), and the depth / feed-count caps.
//
// Plain-ASCII banner so this source diffs as TEXT, never binary.
// ---------------------------------------------------------------------------

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Sinapsi.Opds;
using Sinapsi.Opds.Models;
using Xunit;

namespace Sinapsi.Opds.Tests;

public class OpdsNavTraversalTests
{
    private const string Host = "http://booklore.test:6060";

    /// <summary>A fake handler that serves an authored OPDS feed body per absolute
    /// request URL. Records every fetched URL (in order) + the Authorization header
    /// on each request; a 404 for an unregistered URL surfaces a bad traversal edge.</summary>
    private sealed class UrlFeedHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _feeds = new(StringComparer.Ordinal);
        public List<string> Fetched { get; } = new();
        public List<AuthenticationHeaderValue?> AuthHeaders { get; } = new();

        public UrlFeedHandler Feed(string url, string xml) { _feeds[url] = xml; return this; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Fetched.Add(url);
            AuthHeaders.Add(request.Headers.Authorization);
            if (_feeds.TryGetValue(url, out var xml))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(xml, Encoding.UTF8, "application/atom+xml"),
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    // --- fixture builders (authored, synthetic) -----------------------------

    /// <summary>A navigation feed: entries are catalog links into sub-feeds (kind
    /// per <paramref name="navEntries"/>), ZERO acquisition links. Optionally a
    /// self/start/up back-link to prove the cycle guard + non-descent-rel skip.</summary>
    private static string NavFeed(string selfHref, string? upHref, params (string Title, string Href, bool NavKind)[] navEntries)
    {
        var sb = new StringBuilder();
        sb.Append($"""
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <id>urn:nav:{selfHref}</id>
              <title>Nav {selfHref}</title>
              <link rel="self" href="{selfHref}" type="application/atom+xml;profile=opds-catalog;kind=navigation"/>
              <link rel="start" href="/api/v1/opds" type="application/atom+xml;profile=opds-catalog;kind=navigation"/>
            """);
        if (upHref is not null)
            sb.Append($"""
              <link rel="up" href="{upHref}" type="application/atom+xml;profile=opds-catalog;kind=navigation"/>
            """);
        foreach (var (title, href, navKind) in navEntries)
        {
            var kind = navKind ? "navigation" : "acquisition";
            sb.Append($"""
                  <entry>
                    <id>urn:navlink:{href}</id>
                    <title>{title}</title>
                    <link rel="subsection" href="{href}"
                          type="application/atom+xml;profile=opds-catalog;kind={kind}"/>
                  </entry>
                """);
        }
        sb.Append("</feed>");
        return sb.ToString();
    }

    /// <summary>An acquisition feed page: publication entries (each an EPUB
    /// acquisition link), optional rel="next" pagination, optional self+up nav
    /// back-links (which must NOT be descended into).</summary>
    private static string AcqFeed(string selfHref, string? nextHref, string? upHref, params (string Id, string Title)[] books)
    {
        var sb = new StringBuilder();
        sb.Append($"""
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom" xmlns:dcterms="http://purl.org/dc/terms/">
              <id>urn:acq:{selfHref}</id>
              <title>Acq {selfHref}</title>
              <link rel="self" href="{selfHref}" type="application/atom+xml;profile=opds-catalog;kind=acquisition"/>
              <link rel="start" href="/api/v1/opds" type="application/atom+xml;profile=opds-catalog;kind=navigation"/>
            """);
        if (upHref is not null)
            sb.Append($"""
              <link rel="up" href="{upHref}" type="application/atom+xml;profile=opds-catalog;kind=navigation"/>
            """);
        if (nextHref is not null)
            sb.Append($"""
              <link rel="next" href="{nextHref}" type="application/atom+xml;profile=opds-catalog;kind=acquisition"/>
            """);
        foreach (var (id, title) in books)
            sb.Append($"""
                  <entry>
                    <id>{id}</id>
                    <title>{title}</title>
                    <author><name>Ada Testwriter</name></author>
                    <dcterms:identifier>{id}</dcterms:identifier>
                    <updated>2026-02-01T10:00:00Z</updated>
                    <link rel="http://opds-spec.org/image/thumbnail" href="/covers/{id}.png" type="image/png"/>
                    <link rel="http://opds-spec.org/acquisition" href="/dl/{id}.epub" type="application/epub+zip"/>
                  </entry>
                """);
        sb.Append("</feed>");
        return sb.ToString();
    }

    /// <summary>A BookLore-shaped catalog:
    ///   /api/v1/opds (nav root) -> "All Books" (acq, 2 pages) + "Authors" (nav)
    ///   Authors (nav) -> "Ada" (acq) + "Ben" (acq)
    ///   Each author feed REPEATS books already under All-Books (dedup target) and
    ///   links back "up" to the root (cycle-guard target).
    /// All-Books holds b1..b3 (b3 on page 2); Ada re-lists b1+b2; Ben re-lists b3
    /// and adds a b4 that appears ONLY under an author (proves author feeds are
    /// really traversed, not just All-Books).</summary>
    private static UrlFeedHandler BookLoreCatalog()
    {
        const string root = Host + "/api/v1/opds";
        const string allBooksP1 = Host + "/api/v1/opds/books?page=1";
        const string allBooksP2 = Host + "/api/v1/opds/books?page=2";
        const string authors = Host + "/api/v1/opds/authors";
        const string ada = Host + "/api/v1/opds/authors/ada";
        const string ben = Host + "/api/v1/opds/authors/ben";

        return new UrlFeedHandler()
            .Feed(root, NavFeed("/api/v1/opds", upHref: null,
                ("All Books", "/api/v1/opds/books?page=1", false),
                ("Authors", "/api/v1/opds/authors", true)))
            .Feed(allBooksP1, AcqFeed("/api/v1/opds/books?page=1", "/api/v1/opds/books?page=2", "/api/v1/opds",
                ("urn:isbn:b1", "Book One"),
                ("urn:isbn:b2", "Book Two")))
            .Feed(allBooksP2, AcqFeed("/api/v1/opds/books?page=2", nextHref: null, "/api/v1/opds",
                ("urn:isbn:b3", "Book Three")))
            .Feed(authors, NavFeed("/api/v1/opds/authors", upHref: "/api/v1/opds",
                ("Ada", "/api/v1/opds/authors/ada", false),
                ("Ben", "/api/v1/opds/authors/ben", false)))
            // Ada re-lists b1 + b2 (dupes) and links back UP to root (cycle guard).
            .Feed(ada, AcqFeed("/api/v1/opds/authors/ada", nextHref: null, "/api/v1/opds",
                ("urn:isbn:b1", "Book One"),
                ("urn:isbn:b2", "Book Two")))
            // Ben re-lists b3 (dupe) + adds b4 (author-only book).
            .Feed(ben, AcqFeed("/api/v1/opds/authors/ben", nextHref: null, "/api/v1/opds",
                ("urn:isbn:b3", "Book Three"),
                ("urn:isbn:b4", "Book Four")));
    }

    // --- tests --------------------------------------------------------------

    [Fact]
    public async Task Traverses_nav_root_to_full_deduped_book_set()
    {
        var handler = BookLoreCatalog();
        var client = new OpdsClient(new HttpClient(handler));

        var entries = await client.EnumerateAllAsync(Host + "/api/v1/opds");

        // b1..b4 each once, despite b1/b2/b3 appearing under BOTH All-Books AND an
        // author feed. This is the dedup-by-Id guarantee.
        Assert.Equal(
            new[] { "urn:isbn:b1", "urn:isbn:b2", "urn:isbn:b3", "urn:isbn:b4" },
            entries.Select(e => e.Id).OrderBy(s => s, StringComparer.Ordinal));
        // Every returned entry IS an acquisition entry with an EPUB link (no nav
        // entries leaked into the result).
        Assert.All(entries, e => Assert.NotNull(e.EpubLink));
    }

    [Fact]
    public async Task Cycle_guard_survives_feeds_that_link_back_to_root()
    {
        // The author feeds carry rel="up" -> root and rel="start" -> root; All-Books
        // pages carry rel="up" -> root too. If any of those were followed as a
        // descent edge the root would be re-fetched (infinite loop). Assert each
        // feed URL is fetched at most once.
        var handler = BookLoreCatalog();
        var client = new OpdsClient(new HttpClient(handler));

        _ = await client.EnumerateAllAsync(Host + "/api/v1/opds");

        var distinct = handler.Fetched.Distinct(StringComparer.Ordinal).Count();
        Assert.Equal(distinct, handler.Fetched.Count); // no URL fetched twice
        // The root was fetched exactly once (never re-entered via up/start).
        Assert.Single(handler.Fetched, u => u == Host + "/api/v1/opds");
    }

    [Fact]
    public async Task Sends_basic_auth_on_every_subfetch()
    {
        var handler = BookLoreCatalog();
        var options = new OpdsClientOptions { Username = "opds", Password = "pw" };
        var client = new OpdsClient(new HttpClient(handler), options);

        _ = await client.EnumerateAllAsync(Host + "/api/v1/opds");

        var expected = options.BuildBasicAuthHeader()!.ToString();
        Assert.NotEmpty(handler.AuthHeaders);
        // Every sub-feed fetch (root + nav + acq + author feeds) carried the header.
        Assert.All(handler.AuthHeaders, h =>
        {
            Assert.NotNull(h);
            Assert.Equal(expected, h!.ToString());
        });
    }

    [Fact]
    public async Task Plain_acquisition_root_still_works_unchanged_back_compat()
    {
        // A server whose ROOT is already an acquisition feed (no nav) must behave
        // exactly as pre-M5b: return its entries, descend into nothing.
        const string acqRoot = Host + "/api/v1/opds/books";
        var handler = new UrlFeedHandler()
            .Feed(acqRoot, AcqFeed("/api/v1/opds/books", nextHref: null, upHref: null,
                ("urn:isbn:x1", "Only Book One"),
                ("urn:isbn:x2", "Only Book Two")));
        var client = new OpdsClient(new HttpClient(handler));

        var entries = await client.EnumerateAllAsync(acqRoot);

        Assert.Equal(new[] { "urn:isbn:x1", "urn:isbn:x2" }, entries.Select(e => e.Id));
        Assert.Single(handler.Fetched); // one feed, no descent
    }

    [Fact]
    public async Task Depth_cap_stops_a_pathological_deep_nav_chain()
    {
        // A chain of nav feeds L0 -> L1 -> ... deeper than MaxNavigationDepth, with
        // an acquisition book placed at each level. Books at depth <= cap are
        // collected; the traversal STOPS before descending past the cap (never
        // infinite). Build depth+3 levels to be safely past the cap.
        var handler = new UrlFeedHandler();
        int levels = OpdsClient.MaxNavigationDepth + 3;
        for (int i = 0; i < levels; i++)
        {
            var self = $"/api/v1/opds/l{i}";
            var childNav = i < levels - 1
                ? new (string, string, bool)[] { ($"Down{i + 1}", $"/api/v1/opds/l{i + 1}", true) }
                : Array.Empty<(string, string, bool)>();
            // Each level is a nav feed with ONE nav entry (down) AND one nav entry
            // that is actually an acquisition sub-feed holding this level's book.
            var acqChild = $"/api/v1/opds/l{i}/books";
            var navEntries = new List<(string, string, bool)>
            {
                ($"Books at L{i}", acqChild, false),
            };
            navEntries.AddRange(childNav);
            handler.Feed(Host + self, NavFeed(self, upHref: i == 0 ? null : $"/api/v1/opds/l{i - 1}", navEntries.ToArray()));
            handler.Feed(Host + acqChild, AcqFeed(acqChild, nextHref: null, upHref: self,
                ($"urn:isbn:lvl{i}", $"Level {i} Book")));
        }

        var client = new OpdsClient(new HttpClient(handler));
        var entries = await client.EnumerateAllAsync(Host + "/api/v1/opds/l0");

        // Root is depth 0; its acq child + nav child are depth 1; so books up to the
        // level reachable within MaxNavigationDepth are present, deeper ones are not.
        // The exact reachable count is bounded by the cap; assert it did NOT fetch
        // every one of the (levels) deep feeds AND returned a non-empty prefix.
        Assert.NotEmpty(entries);
        Assert.Contains(entries, e => e.Id == "urn:isbn:lvl0");
        // The deepest level's book is beyond the cap -> not collected.
        Assert.DoesNotContain(entries, e => e.Id == $"urn:isbn:lvl{levels - 1}");
        // And it stopped descending: the deepest nav feed was never fetched.
        Assert.DoesNotContain(Host + $"/api/v1/opds/l{levels - 1}", handler.Fetched);
    }

    [Fact]
    public async Task Feed_budget_cap_stops_crawl_and_logs()
    {
        // A wide catalog with MaxPages set very low: the traversal must stop at the
        // budget and log a warning, never fetch unbounded.
        var handler = BookLoreCatalog();
        string? warned = null;
        var options = new OpdsClientOptions
        {
            MaxPages = 2, // root + one child, then budget exhausted
            Log = (level, msg) => { if (level == "warn") warned = msg; },
        };
        var client = new OpdsClient(new HttpClient(handler), options);

        _ = await client.EnumerateAllAsync(Host + "/api/v1/opds");

        Assert.NotNull(warned);
        Assert.Contains("budget", warned, StringComparison.OrdinalIgnoreCase);
        // It fetched at most the budget's worth of *distinct* feeds (pagination of a
        // single feed counts against the same budget), never the whole catalog.
        Assert.True(handler.Fetched.Count <= options.MaxPages + 1,
            $"fetched {handler.Fetched.Count} feeds, expected <= {options.MaxPages + 1}");
    }

    [Fact]
    public async Task Feed_level_navigation_links_are_followed()
    {
        // Some servers expose sections as FEED-level links (not nav entries). The
        // traversal must follow those too.
        const string root = Host + "/api/v1/opds";
        const string acq = Host + "/api/v1/opds/all";
        var rootWithFeedLink = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <id>urn:root</id>
              <title>Root</title>
              <link rel="self" href="/api/v1/opds" type="application/atom+xml;profile=opds-catalog;kind=navigation"/>
              <link rel="subsection" href="/api/v1/opds/all"
                    type="application/atom+xml;profile=opds-catalog;kind=acquisition" title="All"/>
            </feed>
            """;
        var handler = new UrlFeedHandler()
            .Feed(root, rootWithFeedLink)
            .Feed(acq, AcqFeed("/api/v1/opds/all", nextHref: null, upHref: "/api/v1/opds",
                ("urn:isbn:z1", "Feed-Link Book")));
        var client = new OpdsClient(new HttpClient(handler));

        var entries = await client.EnumerateAllAsync(root);

        Assert.Equal(new[] { "urn:isbn:z1" }, entries.Select(e => e.Id));
        Assert.Contains(acq, handler.Fetched);
    }
}
