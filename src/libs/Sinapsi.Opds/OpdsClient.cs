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
public sealed class OpdsClientOptions
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

    /// <summary>
    /// Enumerate EVERY entry across all pages of a feed, following
    /// <c>rel="next"</c> up to <see cref="OpdsClientOptions.MaxPages"/>. Yields
    /// pages lazily so a caller can stream a large catalog. Guards against a
    /// pagination loop (a next href equal to an already-visited page).
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

    /// <summary>Materialise <see cref="EnumerateEntriesAsync"/> into a full list
    /// (convenience for callers that want the whole catalog for a diff).</summary>
    public async Task<IReadOnlyList<OpdsEntry>> EnumerateAllAsync(
        string feedUrl, CancellationToken ct = default)
    {
        var all = new List<OpdsEntry>();
        await foreach (var e in EnumerateEntriesAsync(feedUrl, ct).ConfigureAwait(false))
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
