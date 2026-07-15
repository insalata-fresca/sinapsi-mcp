// ---------------------------------------------------------------------------
// OpdsClient - a generic OPDS 1.2 client for ANY OPDS server.
//
// Fetches + parses OPDS feeds (navigation + acquisition), follows rel="next"
// pagination, enumerates entries, and downloads an entry's EPUB acquisition
// link. Auth is HTTP Basic OR anonymous, injected via OpdsClientOptions (never
// hardcoded) - BookLore's OPDS (WWW-Authenticate: Basic realm="Booklore OPDS")
// is one instance; the client is not BookLore-specific.
//
// Composition for M4's OpdsSourceScanner:
//   var client = new OpdsClient(httpClient, options);
//   var entries = await client.EnumerateAllAsync(feedUrl, ct);     // follows pagination
//   var diff    = OpdsClient.Diff(previousSnapshot, entries);      // new/changed/removed
//   foreach (var e in diff.New.Concat(diff.Changed))
//       if (e.EpubLink is { } link)
//           bytes = await client.DownloadAsync(link, ct);          // -> EpubExtractor
//
// The client is side-effect-free apart from the HTTP GETs; it owns no mutable
// state and never writes disk. It does NOT dispose the injected HttpClient
// (caller/DI owns its lifetime).
//
// Plain-ASCII banner so this source diffs as TEXT, never binary.
// ---------------------------------------------------------------------------

using System.Net.Http.Headers;
using System.Text;
using Sinapsi.Opds.Models;

namespace Sinapsi.Opds;

/// <summary>
/// Auth + limits for an <see cref="OpdsClient"/>. Anonymous by default; supply
/// <see cref="Username"/> + <see cref="Password"/> for HTTP Basic. Credentials
/// are injected here (from config / a secret manager), never hardcoded in the
/// client.
/// </summary>
public sealed record OpdsClientOptions
{
    /// <summary>HTTP Basic username. Null/empty =&gt; anonymous (no Authorization header).</summary>
    public string? Username { get; init; }

    /// <summary>HTTP Basic password. Ignored when <see cref="Username"/> is null/empty.</summary>
    public string? Password { get; init; }

    /// <summary>Safety cap on pages followed via <c>rel="next"</c> so a
    /// mis-behaving server (self-referential next) cannot loop forever. Default 1000.</summary>
    public int MaxPages { get; init; } = 1000;

    /// <summary>Max EPUB download size in bytes. Default 256 MiB. A response larger
    /// than this fails fast rather than buffering unbounded into memory.</summary>
    public long MaxDownloadBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Optional sink for the traversal's fail-safe warnings (a bound was
    /// hit: depth / feed-budget). Receives <c>(level, message)</c> where level is
    /// <c>"warn"</c>. Null =&gt; silent. Kept as a plain delegate so
    /// <c>Sinapsi.Opds</c> takes no logging dependency; a caller (the indexer) can
    /// bridge it to <c>ILogger</c>. The traversal never throws on a bound — it logs
    /// (if a sink is set) and stops that branch.</summary>
    public Action<string, string>? Log { get; init; }

    /// <summary>True when Basic auth is configured.</summary>
    public bool HasBasicAuth => !string.IsNullOrEmpty(Username);

    /// <summary>Build the <c>Basic base64(user:pass)</c> header value, or null when
    /// anonymous. Exposed so callers/tests can assert the exact header the client sends.</summary>
    public AuthenticationHeaderValue? BuildBasicAuthHeader()
    {
        if (!HasBasicAuth) return null;
        var raw = $"{Username}:{Password}";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
        return new AuthenticationHeaderValue("Basic", b64);
    }
}

/// <summary>
/// Generic OPDS 1.2 client. Idempotent + side-effect-free (only HTTP GETs).
/// Inject an <see cref="HttpClient"/> (its lifetime is the caller's) and
/// <see cref="OpdsClientOptions"/>.
/// </summary>
public sealed class OpdsClient
{
    private readonly HttpClient _http;
    private readonly OpdsClientOptions _options;

    public OpdsClient(HttpClient http, OpdsClientOptions? options = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? new OpdsClientOptions();
    }

    private const string LogLevelWarn = "warn";

    private void _log(string level, string message) => _options.Log?.Invoke(level, message);

    /// <summary>Fetch + parse ONE OPDS feed page. Applies Basic auth when configured.
    /// The returned feed's <see cref="OpdsFeed.NextHref"/> is the absolute next-page
    /// URL (or null). Throws on non-success status or malformed feed XML.</summary>
    public async Task<OpdsFeed> FetchFeedAsync(string feedUrl, CancellationToken ct = default)
    {
        var uri = new Uri(feedUrl, UriKind.Absolute);
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.Authorization = _options.BuildBasicAuthHeader();
        req.Headers.Accept.ParseAdd("application/atom+xml");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var xml = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return OpdsFeedParser.Parse(xml, uri);
    }

    // --- traversal bounds (fail-safe caps) ----------------------------------
    // These bound the OPDS crawl so a mis-behaving / adversarial server can never
    // make the client crawl forever or fan out unboundedly on the shared CT132
    // host. Correctness is dedup-by-Id (over-traversal only costs time); these are
    // the hard stops. MaxPages (OpdsClientOptions) is the overall feed/page budget.

    /// <summary>Deepest navigation nesting the traversal descends (root=0). A real
    /// library is 2-3 deep; 6 tolerates faceted catalogs without infinite descent.</summary>
    public const int MaxNavigationDepth = 6;

    /// <summary>
    /// Enumerate EVERY entry across all pages of a SINGLE feed (no navigation
    /// descent), following <c>rel="next"</c> up to <see cref="OpdsClientOptions.MaxPages"/>.
    /// Yields entries lazily. Guards against a pagination loop (a next href equal to
    /// an already-visited page). This is the flat per-feed primitive; use
    /// <see cref="EnumerateAcquisitionEntriesAsync"/> to traverse navigation feeds.
    /// </summary>
    public async IAsyncEnumerable<OpdsEntry> EnumerateEntriesAsync(
        string feedUrl,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? url = feedUrl;
        int pages = 0;

        while (url is not null && pages < _options.MaxPages && seen.Add(url))
        {
            ct.ThrowIfCancellationRequested();
            var feed = await FetchFeedAsync(url, ct).ConfigureAwait(false);
            foreach (var entry in feed.Entries)
                yield return entry;

            pages++;
            url = feed.NextHref;
        }
    }

    /// <summary>
    /// Traverse an OPDS catalog from <paramref name="rootUrl"/> and yield every
    /// distinct ACQUISITION (publication) entry reachable from it, de-duplicated by
    /// <see cref="OpdsEntry.Id"/>. OPDS roots are almost always NAVIGATION feeds
    /// (BookLore/Calibre-Web/Kavita/Komga): their entries are catalog links (All
    /// Books, Authors, Series, ...) with no acquisition links. This method:
    /// <list type="bullet">
    /// <item>Fetches each feed and PARTITIONS its entries: acquisition entries are
    /// yielded (once, deduped by Id); navigation entries (+ feed-level navigation
    /// links) are queued as child sub-feeds to descend into.</item>
    /// <item>Follows <c>rel="next"</c> pagination at EVERY feed level (via
    /// <see cref="EnumerateEntriesAsync"/> on each feed URL).</item>
    /// <item>Is BOUNDED, fail-safe: a cycle guard (visited absolute feed URLs are
    /// never revisited — servers link back with up/start), a max depth
    /// (<see cref="MaxNavigationDepth"/>), and the overall feed/page budget
    /// <see cref="OpdsClientOptions.MaxPages"/> as a max-feeds-visited cap. Hitting a
    /// bound logs a warning and stops that branch — never an infinite crawl.</item>
    /// </list>
    /// A plain acquisition-root feed (no navigation entries) behaves exactly like
    /// <see cref="EnumerateEntriesAsync"/> did (back-compat): its entries are yielded
    /// and there is nothing to descend into.
    /// </summary>
    public async IAsyncEnumerable<OpdsEntry> EnumerateAcquisitionEntriesAsync(
        string rootUrl,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Cycle guard across the WHOLE traversal: absolute feed URLs already fetched.
        var visitedFeeds = new HashSet<string>(StringComparer.Ordinal);
        // Dedup acquisition entries by Id (a book appears under All-Books AND its
        // Author/Series facet — count it once).
        var yieldedIds = new HashSet<string>(StringComparer.Ordinal);
        // BFS frontier of (feedUrl, depth). BFS so shallow (usually richer) feeds
        // are visited before deep facet chains when a budget cap bites.
        var frontier = new Queue<(string Url, int Depth)>();
        frontier.Enqueue((rootUrl, 0));

        int feedsVisited = 0;

        while (frontier.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (url, depth) = frontier.Dequeue();

            if (!visitedFeeds.Add(url))
                continue; // already crawled this exact feed URL — cycle guard

            if (feedsVisited >= _options.MaxPages)
            {
                _log(LogLevelWarn,
                    $"OPDS traversal hit MaxPages feed-budget ({_options.MaxPages}) at '{url}' — stopping crawl (partial result).");
                break;
            }

            // Fetch this feed + all its rel="next" pages as ONE logical feed. Collect
            // the child sub-feed URLs to descend into AFTER fully paging this level.
            var childUrls = new List<string>();
            OpdsFeed page;
            string? pageUrl = url;
            var pageSeen = new HashSet<string>(StringComparer.Ordinal);
            int pagesHere = 0;

            while (pageUrl is not null && pagesHere < _options.MaxPages && pageSeen.Add(pageUrl))
            {
                ct.ThrowIfCancellationRequested();
                page = await FetchFeedAsync(pageUrl, ct).ConfigureAwait(false);
                feedsVisited++;

                foreach (var entry in page.Entries)
                {
                    if (entry.IsAcquisition)
                    {
                        // Acquisition (publication) entry — yield once, deduped by Id.
                        if (yieldedIds.Add(entry.Id))
                            yield return entry;
                    }
                    else
                    {
                        // Navigation entry — queue each of its descent links as a child feed.
                        foreach (var nav in entry.NavigationLinks)
                            if (!string.IsNullOrEmpty(nav.Href))
                                childUrls.Add(nav.Href);
                    }
                }

                // Feed-LEVEL navigation links (sections exposed as feed links, not entries).
                foreach (var nav in page.NavigationLinks)
                    if (!string.IsNullOrEmpty(nav.Href))
                        childUrls.Add(nav.Href);

                pagesHere++;
                pageUrl = page.NextHref;
            }

            // Enqueue children for the next depth — unless the depth cap is reached.
            if (childUrls.Count > 0)
            {
                if (depth + 1 > MaxNavigationDepth)
                {
                    _log(LogLevelWarn,
                        $"OPDS traversal hit MaxNavigationDepth ({MaxNavigationDepth}) at '{url}' — not descending into {childUrls.Count} sub-feed(s).");
                }
                else
                {
                    foreach (var child in childUrls)
                        if (!visitedFeeds.Contains(child)) // cheap pre-filter; Add still guards on dequeue
                            frontier.Enqueue((child, depth + 1));
                }
            }
        }
    }

    /// <summary>Materialise <see cref="EnumerateAcquisitionEntriesAsync"/> into a
    /// full de-duplicated list of ACQUISITION entries reachable from
    /// <paramref name="feedUrl"/> — traversing navigation sub-feeds. This is the
    /// method M4's <c>OpdsSourceScanner</c> calls; its signature is unchanged, but it
    /// now traverses (a nav root yields the whole library instead of 0 books). A
    /// plain acquisition root still returns exactly its entries (back-compat).</summary>
    public async Task<IReadOnlyList<OpdsEntry>> EnumerateAllAsync(
        string feedUrl, CancellationToken ct = default)
    {
        var all = new List<OpdsEntry>();
        await foreach (var e in EnumerateAcquisitionEntriesAsync(feedUrl, ct).ConfigureAwait(false))
            all.Add(e);
        return all;
    }

    /// <summary>
    /// Download the bytes behind an acquisition link (the EPUB). Applies Basic auth,
    /// enforces <see cref="OpdsClientOptions.MaxDownloadBytes"/>, and returns the
    /// full byte array (an EPUB is a zip - the caller streams it into
    /// <c>EpubExtractor</c>). Throws on non-success status or oversize.
    /// </summary>
    public async Task<byte[]> DownloadAsync(OpdsLink link, CancellationToken ct = default)
    {
        if (link is null) throw new ArgumentNullException(nameof(link));
        if (string.IsNullOrEmpty(link.Href)) throw new ArgumentException("Link has no href.", nameof(link));
        return await DownloadAsync(link.Href, ct).ConfigureAwait(false);
    }

    /// <summary>Download an acquisition href's bytes (see <see cref="DownloadAsync(OpdsLink,CancellationToken)"/>).</summary>
    public async Task<byte[]> DownloadAsync(string href, CancellationToken ct = default)
    {
        var uri = new Uri(href, UriKind.Absolute);
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.Authorization = _options.BuildBasicAuthHeader();

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var declared = resp.Content.Headers.ContentLength;
        if (declared.HasValue && declared.Value > _options.MaxDownloadBytes)
            throw new InvalidOperationException(
                $"OPDS download exceeds MaxDownloadBytes ({declared.Value} > {_options.MaxDownloadBytes}).");

        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = declared.HasValue ? new MemoryStream((int)Math.Min(declared.Value, int.MaxValue)) : new MemoryStream();

        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > _options.MaxDownloadBytes)
                throw new InvalidOperationException(
                    $"OPDS download exceeds MaxDownloadBytes (> {_options.MaxDownloadBytes}).");
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Diff a freshly-enumerated entry set against a prior snapshot of
    /// <c>{entry Id -&gt; change-token}</c>. The change-token is the entry's
    /// <see cref="OpdsEntry.Updated"/> rendered as a round-trip ("o") string
    /// (falls back to the entry Id when Updated is absent) - so an entry whose
    /// Updated advanced counts as "changed". Static, side-effect-free.
    /// </summary>
    public static OpdsDiff Diff(
        IReadOnlyDictionary<string, string> previous,
        IReadOnlyList<OpdsEntry> current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        var newEntries = new List<OpdsEntry>();
        var changed = new List<OpdsEntry>();
        var currentIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in current)
        {
            currentIds.Add(entry.Id);
            var token = ChangeToken(entry);
            if (!previous.TryGetValue(entry.Id, out var prevToken))
                newEntries.Add(entry);
            else if (!string.Equals(prevToken, token, StringComparison.Ordinal))
                changed.Add(entry);
        }

        var removed = previous.Keys.Where(id => !currentIds.Contains(id)).ToList();
        return new OpdsDiff(newEntries, changed, removed);
    }

    /// <summary>Build the change-token snapshot from a current entry set - hand this
    /// to <see cref="Diff"/> next time as the <c>previous</c> argument.</summary>
    public static IReadOnlyDictionary<string, string> Snapshot(IReadOnlyList<OpdsEntry> entries)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in entries)
            map[e.Id] = ChangeToken(e);
        return map;
    }

    /// <summary>The per-entry change-token used by <see cref="Diff"/> +
    /// <see cref="Snapshot"/>: the UTC "o" round-trip of Updated, else the entry Id.</summary>
    public static string ChangeToken(OpdsEntry entry) =>
        entry.Updated is { } u
            ? u.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture)
            : entry.Id;
}
